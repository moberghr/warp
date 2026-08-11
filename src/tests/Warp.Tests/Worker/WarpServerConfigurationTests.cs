using Microsoft.Extensions.Configuration;
using Shouldly;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class WarpServerConfigurationTests
{
    // RED until the Queues setter stops treating a binder rebind as an explicit choice.
    // ConfigurationBinder invokes every public setter with a rebound copy of the CURRENT value even when
    // the bound section carries no matching key at all — so "the setter ran" cannot mean "the user chose".
    // Without the guard, any deployment that does config.GetSection("Warp").Bind(opt) gets the stranding
    // bug back: publishes land on DefaultQueue while the implicit group still polls the literal "default".
    [TimedFact]
    public void GetEffectiveWorkerGroups_BoundFromConfigWithoutQueuesKey_StillFollowsDefaultQueue()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Warp:DefaultQueue"] = "orders" })
            .Build();

        var opt = new WarpServerConfiguration();
        config.GetSection("Warp").Bind(opt);

        var groups = opt.GetEffectiveWorkerGroups();

        groups[0].Queues.ShouldContain("orders");
        groups[0].Queues.ShouldNotContain("default");
    }

    [TimedFact]
    public void GetEffectiveWorkerGroups_BoundFromConfigWithQueuesKey_UsesTheBoundQueues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Warp:DefaultQueue"] = "orders",
                ["Warp:Queues:0"] = "high",
                ["Warp:Queues:1"] = "low",
            })
            .Build();

        var opt = new WarpServerConfiguration();
        config.GetSection("Warp").Bind(opt);

        var groups = opt.GetEffectiveWorkerGroups();

        groups[0].Queues.ShouldContain("high");
        groups[0].Queues.ShouldContain("low");
        groups[0].Queues.ShouldNotContain("orders");
    }

    // RED until GetEffectiveWorkerGroups can take the RESOLVED DefaultQueue. With AddWarp called first
    // (its builder wins the IOptions<WarpConfiguration> TryAddSingleton), the Publisher publishes onto
    // AddWarp's DefaultQueue while the server builder's own DefaultQueue is still "default" — the
    // substitution must follow the value publishes actually use, not the server builder's copy.
    [TimedFact]
    public void GetEffectiveWorkerGroups_WithResolvedDefaultQueueOverride_FollowsTheResolvedValue()
    {
        var config = new WarpServerConfiguration();

        var groups = config.GetEffectiveWorkerGroups("orders");

        groups[0].Queues.ShouldContain("orders");
        groups[0].Queues.ShouldNotContain("default");
    }

    // RED until the implicit default group follows DefaultQueue. The Publisher now honours
    // WarpConfiguration.DefaultQueue (jobs publish onto it), but Queues still hardcoded ["default"] — so
    // `opt.DefaultQueue = "orders"` with Queues untouched published every job onto a queue no worker
    // polled, silently, forever. The implicit group must poll where untargeted publishes actually land.
    [TimedFact]
    public void GetEffectiveWorkerGroups_WithDefaultQueueSetAndQueuesUntouched_PollsTheDefaultQueue()
    {
        var config = new WarpServerConfiguration
        {
            DefaultQueue = "orders",
        };

        var groups = config.GetEffectiveWorkerGroups();

        groups.Count.ShouldBe(1);
        groups[0].Queues.ShouldContain("orders");
        groups[0].Queues.ShouldNotContain("default");
    }

    // An explicit Queues is an explicit decision — DefaultQueue must not sneak into it. A deployment that
    // splits publishing (DefaultQueue = "orders") from a worker dedicated to other queues is a supported
    // shape, and silently appending the default queue would widen that worker's claim set.
    [TimedFact]
    public void GetEffectiveWorkerGroups_WithExplicitQueues_DoesNotFollowDefaultQueue()
    {
        var config = new WarpServerConfiguration
        {
            DefaultQueue = "orders",
            Queues = ["high", "low"],
        };

        var groups = config.GetEffectiveWorkerGroups();

        groups[0].Queues.ShouldContain("high");
        groups[0].Queues.ShouldContain("low");
        groups[0].Queues.ShouldNotContain("orders");
    }

    [TimedFact]
    public void GetEffectiveWorkerGroups_WithNothingSet_PollsDefault()
    {
        var config = new WarpServerConfiguration();

        var groups = config.GetEffectiveWorkerGroups();

        groups[0].Queues.ShouldContain("default");
    }

    [TimedFact]
    public void GetEffectiveWorkerGroups_PropagatesBackoffProperties()
    {
        var config = new WarpServerConfiguration
        {
            PollingInterval = TimeSpan.FromSeconds(2),
            MaxPollingInterval = TimeSpan.FromSeconds(45),
            PollingIntervalFactor = 3.0,
        };

        var groups = config.GetEffectiveWorkerGroups();

        groups.Count.ShouldBe(1);
        groups[0].PollingInterval.ShouldBe(TimeSpan.FromSeconds(2));
        groups[0].MaxPollingInterval.ShouldBe(TimeSpan.FromSeconds(45));
        groups[0].PollingIntervalFactor.ShouldBe(3.0);
    }
}

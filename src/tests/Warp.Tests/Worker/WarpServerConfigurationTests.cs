using Shouldly;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class WarpServerConfigurationTests
{
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

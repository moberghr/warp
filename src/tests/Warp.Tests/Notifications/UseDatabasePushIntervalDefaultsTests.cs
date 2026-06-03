using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.Tests.Notifications;

// Pinned behavior: when UseDatabasePush() is called, the signal-driven server tasks
// (MessageRouter, Orchestrator) can run on a backstop cadence because push wakes them
// immediately. UseDatabasePush bumps the default polling intervals on those tasks so
// idle bookkeeping doesn't dominate the DB query rate. Explicit overrides win — bumping
// only fires when the value is still at its class default.
[Trait("Category", "NoDb")]
public class UseDatabasePushIntervalDefaultsTests
{
    [Fact]
    public void UseDatabasePush_AtDefaultMessageRoutingInterval_BumpsToBackstop()
    {
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.MessageRoutingInterval.ShouldBe(TimeSpan.FromSeconds(1), "precondition: class default is 1s");
                opt.UseDatabasePush();
                observed = opt.MessageRoutingInterval;
            });

        observed.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void UseDatabasePush_AtDefaultOrchestrationInterval_BumpsToBackstop()
    {
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.OrchestrationInterval.ShouldBe(TimeSpan.FromSeconds(10), "precondition: class default is 10s");
                opt.UseDatabasePush();
                observed = opt.OrchestrationInterval;
            });

        observed.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void UseDatabasePush_ExplicitMessageRoutingInterval_PreservesUserValue()
    {
        // Order: explicit value set BEFORE UseDatabasePush. We can't reliably detect "user
        // set X exactly to the class default" — caller knows about this; if they want a
        // tight cadence WITH push, they set it AFTER UseDatabasePush. Here we pin that the
        // common case (any non-default value before UseDatabasePush) is preserved.
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.MessageRoutingInterval = TimeSpan.FromSeconds(5);
                opt.UseDatabasePush();
                observed = opt.MessageRoutingInterval;
            });

        observed.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void UseDatabasePush_ExplicitOrchestrationInterval_PreservesUserValue()
    {
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.OrchestrationInterval = TimeSpan.FromSeconds(20);
                opt.UseDatabasePush();
                observed = opt.OrchestrationInterval!.Value;
            });

        observed.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void UseDatabasePush_AtDefaultMaxPollingInterval_BumpsToBackstop()
    {
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.MaxPollingInterval.ShouldBe(TimeSpan.FromSeconds(30), "precondition: class default is 30s");
                opt.UseDatabasePush();
                observed = opt.MaxPollingInterval;
            });

        observed.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void UseDatabasePush_ExplicitMaxPollingInterval_PreservesUserValue()
    {
        TimeSpan? observed = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.MaxPollingInterval = TimeSpan.FromSeconds(45);
                opt.UseDatabasePush();
                observed = opt.MaxPollingInterval;
            });

        observed.ShouldBe(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void WarpWorkerConfiguration_DefaultCounterAggregationInterval_IsOneMinute()
    {
        // E: the class default was 5s, bumped to 60s because counter aggregation isn't
        // latency-critical and the dashboard reads tolerate 1-minute freshness.
        new WarpWorkerConfiguration().CounterAggregationInterval
            .ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void WarpWorkerConfiguration_DefaultServerTaskBatchSize_IsOneThousand()
    {
        // Pins the bounded-batching ceiling — a silent change to the default would either
        // hurt throughput (too small) or hold the orchestration lock too long (too large).
        // Raised from 100 → 1000 in fix/task-cadence: combined with batched commits inside
        // MessageRouter the per-iteration cost is bounded by one SaveChanges round-trip,
        // and 1000 is the right balance between drain rate and multi-server fairness.
        new WarpWorkerConfiguration().ServerTaskBatchSize.ShouldBe(1000);
    }

    [Fact]
    public void UseDatabasePush_ThenExplicitOverride_LatestAssignmentWins()
    {
        // Documented contract: when you want a tighter cadence WITH push, set the interval
        // AFTER UseDatabasePush(). The bump only fires inside UseDatabasePush() so any later
        // assignment is preserved by ordinary property semantics.
        TimeSpan? routing = null;
        TimeSpan? orchestration = null;

        new ServiceCollection()
            .AddDbContext<TestContext>(o => o.UseNpgsql("Host=x;Database=x;Username=x;Password=x"))
            .AddWarpWorker<TestContext>(opt =>
            {
                opt.UsePostgreSql();
                opt.UseDatabasePush();

                // After the bump, override with explicit values — these must stick.
                opt.MessageRoutingInterval = TimeSpan.FromSeconds(2);
                opt.OrchestrationInterval = TimeSpan.FromSeconds(3);
                routing = opt.MessageRoutingInterval;
                orchestration = opt.OrchestrationInterval;
            });

        routing.ShouldBe(TimeSpan.FromSeconds(2));
        orchestration.ShouldBe(TimeSpan.FromSeconds(3));
    }
}

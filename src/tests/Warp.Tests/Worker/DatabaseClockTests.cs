using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class DatabaseClockTests
{
    private static readonly DateTimeOffset LocalNow = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [TimedFact]
    public void Record_DatabaseReadMidwayThroughTheRoundTrip_CorrectsNowByTheOffsetFromTheMidpoint()
    {
        // Arrange — sent at 12:00:00, answered at 12:00:02: the database read its clock about 12:00:01.
        var local = new FakeTimeProvider(LocalNow);
        var clock = new DatabaseClock(local);
        var databaseNow = LocalNow.AddSeconds(1).AddHours(-3).UtcDateTime;

        // Act
        var offset = clock.Record(LocalNow, LocalNow.AddSeconds(2), databaseNow);

        // Assert
        offset.ShouldBe(TimeSpan.FromHours(-3));
        clock.GetUtcNow().ShouldBe(LocalNow.AddHours(-3));
    }

    [TimedFact]
    public void GetUtcNow_BeforeAnyMeasurement_IsTheLocalClock()
    {
        // Arrange
        var clock = new DatabaseClock(new FakeTimeProvider(LocalNow));

        // Act
        var now = clock.GetUtcNow();

        // Assert
        now.ShouldBe(LocalNow);
    }

    [TimedFact]
    public void GetUtcNow_WhileAServerFromBefore80IsAlive_IsTheLocalClockButTheOffsetIsStillMeasured()
    {
        // Arrange — rule 2.20: a 7.7 server compares its own clock, so this one does too while it is alive.
        var clock = new DatabaseClock(new FakeTimeProvider(LocalNow), new LegacyServerPresence(alive: true));
        clock.Record(LocalNow, LocalNow, LocalNow.AddHours(-3).UtcDateTime);

        // Act
        var now = clock.GetUtcNow();

        // Assert
        now.ShouldBe(LocalNow);
        clock.Offset.ShouldBe(TimeSpan.FromHours(-3));
    }

    [TimedFact]
    public void GetUtcNow_OnceNoServerFromBefore80IsAlive_AppliesTheOffsetMeasuredMeanwhile()
    {
        // Arrange — measured while the old server was alive, then a claim reported it gone.
        var legacyServers = new LegacyServerPresence(alive: true);
        var clock = new DatabaseClock(new FakeTimeProvider(LocalNow), legacyServers);
        clock.Record(LocalNow, LocalNow, LocalNow.AddHours(-3).UtcDateTime);
        legacyServers.Report(alive: false);

        // Act
        var now = clock.GetUtcNow();

        // Assert
        now.ShouldBe(LocalNow.AddHours(-3));
    }

    [TimedFact]
    public void GetTimestamp_AfterAMeasurement_StaysOnTheLocalClock()
    {
        // Arrange — durations and timers must not jump when the offset changes.
        var local = new FakeTimeProvider(LocalNow);
        var clock = new DatabaseClock(local);
        clock.Record(LocalNow, LocalNow, LocalNow.AddHours(5).UtcDateTime);

        // Act
        var timestamp = clock.GetTimestamp();

        // Assert
        timestamp.ShouldBe(local.GetTimestamp());
    }

    // Every server component DI builds must read the database's clock (#350): a constructor that takes a plain
    // TimeProvider gets the host's local clock and silently compares another server's times on it. The types
    // listed are built by their host, which passes its own (keyed) clock down.
    [TimedFact]
    public void Constructors_EveryDiResolvedServerComponent_TakesTheDatabaseClock()
    {
        // Arrange
        string[] builtByTheirHost =
        [
            "WarpWorker`1", "WarpWorkerService`1", "WarpDispatcher`1", "WarpDispatcherWorker`1", "RunningJobMonitor", "CompletionFlusher`1",
            "CompletionBatch", "BackgroundServiceLogCollector", "PerServerTaskLoop`1", "ClusterTaskScheduler`1",
        ];

        // Act
        var unkeyed = typeof(WarpServerConfiguration).Assembly.GetTypes()
            .Where(x => !builtByTheirHost.Contains(x.Name))
            .SelectMany(x => x.GetConstructors())
            .SelectMany(x => x.GetParameters())
            .Where(x => x.ParameterType == typeof(TimeProvider))
            .Where(x => x.GetCustomAttribute<FromKeyedServicesAttribute>()?.Key is not DatabaseClock.Key)
            .Select(x => $"{x.Member.DeclaringType!.Name}({x.Name})")
            .ToList();

        // Assert
        unkeyed.ShouldBeEmpty();
    }
}

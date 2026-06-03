using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

/// <summary>
/// Integration tests that exercise the full ILoggerProvider → BackgroundServiceLogCollector →
/// BackgroundServiceLog DB pipeline using a real running service and a real database.
/// </summary>
[GenerateDatabaseTests]
public abstract class LogCaptureTestsBase : IntegrationTestBase
{
    protected LogCaptureTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    /// <summary>
    /// Polls until at least <paramref name="minCount"/> <see cref="BackgroundServiceLog"/> rows
    /// matching <paramref name="predicate"/> exist for the given service, then returns all rows.
    /// Uses a tight 50ms cadence. A signal-driven approach is not available here because the
    /// log-flush timer fires inside <c>BackgroundServiceLogCollector</c> on a <c>PeriodicTimer</c>
    /// and there is no test-visible flush-completion signal — polling is the only viable option.
    /// </summary>
    private async Task<List<BackgroundServiceLog>> WaitForLogs(
        string serviceName,
        Func<BackgroundServiceLog, bool> predicate,
        int minCount,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(10);
        var deadline = DateTime.UtcNow + effectiveTimeout;
        var ct = Xunit.TestContext.Current.CancellationToken;

        while (DateTime.UtcNow < deadline)
        {
            var ctx = Fixture.CreateContext();
            var logs = await ctx.Set<BackgroundServiceLog>()
                .Where(x => x.ServiceName == serviceName)
                .AsNoTracking()
                .ToListAsync(ct);

            if (logs.Count(predicate) >= minCount)
            {
                return logs;
            }

            // 50ms cadence — tighter than the 100ms flush interval so we don't overshoot by a
            // full flush cycle. Task.Delay is required here; see method summary above.
            await Task.Delay(50, ct);
        }

        var ctx2 = Fixture.CreateContext();
        var allRows = await ctx2.Set<BackgroundServiceLog>()
            .Where(x => x.ServiceName == serviceName)
            .AsNoTracking()
            .ToListAsync(ct);

        throw new TimeoutException(
            $"BackgroundServiceLog for '{serviceName}' did not accumulate {minCount} row(s) matching the predicate within {effectiveTimeout}. " +
            $"Rows found: {allRows.Count}. Messages: {string.Join(", ", allRows.Select(r => $"[{r.Level}/{r.Source}] {r.Message}"))}");
    }

    [TimedFact(20_000)]
    public async Task UserLogAtInformation_CapturedToDb()
    {
        var signal = new LoggingServiceSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<LoggingService>(),
            configureServices: services => services.AddSingleton(signal));

        // Wait until the service has emitted its log messages.
        await signal.Logged.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Poll until the flush timer commits the row.
        var logs = await WaitForLogs(
            nameof(LoggingService),
            l => l.Source == BackgroundServiceLogSource.User && l.Level == LogLevel.Information,
            minCount: 1);

        var infoRow = logs.FirstOrDefault(
            l => l.Source == BackgroundServiceLogSource.User
                && l.Level == LogLevel.Information
                && l.Message.Contains("hello", StringComparison.OrdinalIgnoreCase));

        infoRow.ShouldNotBeNull("Information-level user log should be captured");
        infoRow!.Message.ShouldContain("42");

        signal.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task UserLogAtWarning_CapturedToDb()
    {
        var signal = new LoggingServiceSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<LoggingService>(),
            configureServices: services => services.AddSingleton(signal));

        await signal.Logged.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        var logs = await WaitForLogs(
            nameof(LoggingService),
            l => l.Source == BackgroundServiceLogSource.User && l.Level == LogLevel.Warning,
            minCount: 1);

        var warnRow = logs.FirstOrDefault(
            l => l.Source == BackgroundServiceLogSource.User && l.Level == LogLevel.Warning);

        warnRow.ShouldNotBeNull("Warning-level user log should be captured");
        warnRow!.Message.ShouldContain("warn", Case.Insensitive);

        signal.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task UserLogAtDebug_NotCaptured_DefaultThreshold()
    {
        var signal = new LoggingServiceSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<LoggingService>(),
            configureServices: services => services.AddSingleton(signal));

        await signal.Logged.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // Wait until at least the Information row has flushed — proves the flush ran.
        await WaitForLogs(
            nameof(LoggingService),
            l => l.Source == BackgroundServiceLogSource.User && l.Level == LogLevel.Information,
            minCount: 1);

        var ctx = Fixture.CreateContext();
        var debugRows = await ctx.Set<BackgroundServiceLog>()
            .Where(x => x.ServiceName == nameof(LoggingService))
            .Where(x => x.Source == BackgroundServiceLogSource.User)
            .Where(x => x.Level == LogLevel.Debug)
            .AsNoTracking()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        debugRows.ShouldBeEmpty("Debug rows should be filtered out by the default Information threshold");

        signal.CanFinish.Release();
    }

    [TimedFact(20_000)]
    public async Task LifecycleEventsCaptured_WithSourceLifecycle()
    {
        var signal = new LoggingServiceSignal();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddBackgroundService<LoggingService>(),
            configureServices: services => services.AddSingleton(signal));

        await signal.Logged.WaitAsync(Xunit.TestContext.Current.CancellationToken);

        // The supervisor emits a lifecycle "Started" (or "LeaseAcquired") event before invoking
        // user code. Wait until at least one Lifecycle row appears.
        var logs = await WaitForLogs(
            nameof(LoggingService),
            l => l.Source == BackgroundServiceLogSource.Lifecycle,
            minCount: 1);

        var lifecycleRows = logs
            .Where(l => l.Source == BackgroundServiceLogSource.Lifecycle)
            .ToList();

        lifecycleRows.ShouldNotBeEmpty("At least one Lifecycle row (Started) should be captured");

        signal.CanFinish.Release();
    }
}

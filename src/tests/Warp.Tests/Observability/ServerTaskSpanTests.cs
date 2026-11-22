using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Logging;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker.Services;

namespace Warp.Tests.Observability;

[GenerateDatabaseTests]
public abstract class ServerTaskSpanTestsBase : IntegrationTestBase
{
    protected ServerTaskSpanTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task GivenRunningServer_WhenLoopIterates_ThenServerTaskSpansAreEmitted()
    {
        using var harness = new ActivityListenerHarness();

        await using var server = await WarpTestServer.StartAsync(Fixture);

        // Drive at least one full pass of the orchestrator/heartbeat by publishing a job and
        // waiting for completion — that exercises the auto-run loop path that owns the
        // warp.server_task <Name> activity wrapping. Then poll for at least one server-task
        // span to be captured. Without the poll, a fast happy-path job (sub-100ms total) can
        // complete and exit the test before any auto-run server task has had a chance to fire,
        // which produced an intermittent "should not be empty but was" flake on heavily-loaded
        // SQL Server runs.
        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        await server.WaitForCompletion();

        List<Activity> serverTaskSpans = [];
        await WarpTestServer.WaitUntil(
            "A server-task span being captured",
            () =>
            {
                serverTaskSpans = [.. harness.Captured.Where(a => a.OperationName.StartsWith("warp.server_task ", StringComparison.Ordinal))];

                return Task.FromResult(serverTaskSpans.Count > 0);
            });

        foreach (var span in serverTaskSpans)
        {
            span.Kind.ShouldBe(ActivityKind.Internal);
            span.GetTagItem(WarpTelemetryAttributes.WarpTaskName).ShouldNotBeNull();
            span.GetTagItem(WarpTelemetryAttributes.WarpTaskLockHeld).ShouldNotBeNull();
        }
    }

    [TimedFact]
    public async Task GivenThrowingServerTask_WhenLoopIterates_ThenSpanRecordsErrorTypeAndStatusError()
    {
        using var harness = new ActivityListenerHarness();

        // Build the throwing task on the test instance so the firstThrow TCS isn't shared across
        // _PostgreSql / _SqlServer subclasses running in parallel — register the same instance as
        // a singleton in DI; ServerTaskHost picks it up via GetServices<IServerTask>().
        var throwingTask = new ThrowingServerTask();

        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: null,
            configureServices: services => services.AddSingleton<IServerTask>(throwingTask));

        // Wait for the throwing task's loop to actually fire at least once (DefaultInterval is
        // 250ms) and emit + close its activity. Bounded by the test's TimedFact deadline.
        await throwingTask.WaitForFirstThrowAsync(TimeSpan.FromSeconds(10));

        // Activity is captured on stop; allow a tick for the loop's catch/finally to dispose it.
        Activity? errorSpan = null;
        await WarpTestServer.WaitUntil(
            "The throwing server task's span being captured",
            () =>
            {
                errorSpan = harness.FirstByName($"warp.server_task {ThrowingServerTask.TaskName}");

                return Task.FromResult(errorSpan != null);
            },
            TimeSpan.FromSeconds(5));

        errorSpan.ShouldNotBeNull();
        errorSpan.Status.ShouldBe(ActivityStatusCode.Error);
        errorSpan.GetTagItem(WarpTelemetryAttributes.ErrorType).ShouldBe(typeof(InvalidOperationException).FullName);
        errorSpan.GetTagItem(WarpTelemetryAttributes.WarpTaskLockHeld).ShouldNotBeNull();
    }

    private sealed class ThrowingServerTask : IServerTask
    {
        public const string TaskName = "ThrowingForTest";
        private readonly TaskCompletionSource _firstThrow = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => TaskName;

        public string? LockKey => null;

        public TimeSpan? DefaultInterval => TimeSpan.FromMilliseconds(250);

        public Task<string?> ExecuteAsync(CancellationToken ct)
        {
            _firstThrow.TrySetResult();

            throw new InvalidOperationException("test-only failure");
        }

        public Task WaitForFirstThrowAsync(TimeSpan timeout) => _firstThrow.Task.WaitAsync(timeout);
    }
}

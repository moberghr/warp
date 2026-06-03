using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Tests.Fixtures;

[GenerateDatabaseTests]
public abstract class PreStopDumpCaptureTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected PreStopDumpCaptureTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task DisposeAsync_WritesPreStopDumpToStderr_WithSeededJobRows()
    {
        // Redirect stderr for this test so we can assert what WarpTestServer.DisposeAsync
        // emits. Restore the original writer in finally — leaving Console.Error swapped would
        // break stderr capture for any subsequent test sharing this xunit process.
        var capture = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(capture);
        try
        {
            var server = await WarpTestServer.StartAsync(_fixture);

            var jobId = Guid.NewGuid();
            var ctx = _fixture.CreateContext();
            ctx.Set<Job>().Add(new Job
            {
                Id = jobId,
                Kind = JobKind.Job,
                CurrentState = State.Scheduled,
                Queue = "default",
                Type = "TestType",
                Message = "{}",
                CreateTime = DateTime.UtcNow,

                // Far-future ScheduleTime so neither ScheduledJobActivation nor a worker can
                // touch it during the brief window before disposal.
                ScheduleTime = DateTime.UtcNow.AddHours(1),
            });
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            await server.DisposeAsync();
        }
        finally
        {
            Console.SetError(originalErr);
        }

        var dump = capture.ToString();
        dump.ShouldContain("[WARP-PRE-STOP-DIAG");
        dump.ShouldContain("state captured before IHost.StopAsync");
        dump.ShouldContain("Scheduled");
    }

    [TimedFact]
    public async Task DisposeAsync_EmitsHeader_EvenWhenNoJobsExist()
    {
        // Empty DB: header still emitted so flake hunters can see the dump fired and confirm
        // there really was no in-flight state at the failure moment (vs the dump silently
        // failing).
        var capture = new StringWriter();
        var originalErr = Console.Error;
        Console.SetError(capture);
        try
        {
            await using var server = await WarpTestServer.StartAsync(_fixture);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        capture.ToString().ShouldContain("[WARP-PRE-STOP-DIAG");
    }
}

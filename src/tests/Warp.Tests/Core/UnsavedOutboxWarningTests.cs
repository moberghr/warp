using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Core.Logging;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

/// <summary>
/// Covers the development-time diagnostic that catches the silent outbox footgun: staging
/// jobs/messages via <see cref="IPublisher"/> but ending the scope without
/// <c>SaveChangesAsync</c> (the work is silently discarded). The check runs on publisher dispose.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class UnsavedOutboxWarningTests
{
    [TimedFact]
    public async Task Dispose_WithUnsavedStagedJobs_LogsSingleWarningWithCount()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());

        await publisher.Publish(new SingleHandlerMessage());

        // Scope ends WITHOUT SaveChangesAsync — the staged job would be silently discarded.
        publisher.Dispose();

        sink.Warnings.Count.ShouldBe(1);
        sink.Warnings[0].ShouldContain("1 job(s)/message(s)");
        sink.Warnings[0].ShouldContain("SaveChangesAsync");
    }

    [TimedFact]
    public async Task Dispose_AfterSaveChanges_DoesNotWarn()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());

        await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        publisher.Dispose();

        sink.Warnings.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Dispose_WhenNothingStaged_DoesNotWarn()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());

        publisher.Dispose();

        sink.Warnings.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Dispose_WhenCheckDisabled_DoesNotWarn()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration { WarnOnUnsavedStagedJobs = false });

        await publisher.Publish(new SingleHandlerMessage());

        publisher.Dispose();

        sink.Warnings.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Dispose_InsideWorkerHandlerScope_DoesNotWarn()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());

        // Simulate executing inside a worker handler: the worker owns the commit, so an unsaved
        // staged job here is not the caller's footgun and must not warn (false-positive guard).
        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = Guid.NewGuid(),
            TraceId = Guid.NewGuid(),
        };

        try
        {
            await publisher.Publish(new SingleHandlerMessage());

            publisher.Dispose();

            sink.Warnings.ShouldBeEmpty();
        }
        finally
        {
            JobExecutionContext.Current = null;
        }
    }

    [TimedFact]
    public async Task Dispose_WithUnsavedBatch_LogsWarning()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var provider = NewProvider(sink);
        var publisher = new BatchPublisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, provider, TestTasks.NullTransport, TestTasks.NullSignals);

        await publisher.StartNew([new SingleHandlerJob()]);

        publisher.Dispose();

        sink.Warnings.Count.ShouldBe(1);
        sink.Warnings[0].ShouldContain("SaveChangesAsync");
    }

    private static TestContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseInMemoryDatabase($"warp-outbox-{Guid.NewGuid():N}")
            .Options;

        return new TestContext(options);
    }

    private static ServiceProvider NewProvider(WarningSink sink)
    {
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(new CapturingLoggerProvider(sink)));

        return services.BuildServiceProvider();
    }

    private static Publisher<TestContext> NewPublisher(TestContext ctx, WarningSink sink, WarpConfiguration configuration)
    {
        return new Publisher<TestContext>(ctx, Options.Create(configuration), TimeProvider.System, NewProvider(sink), TestTasks.NullTransport, TestTasks.NullSignals);
    }

    private sealed class SingleHandlerJob : IJob;

    private sealed class WarningSink
    {
        public List<string> Warnings { get; } = [];
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly WarningSink _sink;

        public CapturingLoggerProvider(WarningSink sink) => _sink = sink;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_sink);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly WarningSink _sink;

        public CapturingLogger(WarningSink sink) => _sink = sink;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _sink.Warnings.Add(formatter(state, exception));
            }
        }
    }
}

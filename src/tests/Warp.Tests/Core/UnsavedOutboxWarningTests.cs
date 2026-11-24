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
    public async Task Dispose_InsideWorkerHandlerScope_LogsWarning()
    {
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());

        // Simulate executing inside a worker handler. Since 8.0 the worker does not save a handler's
        // context (rule 6.17), so a job staged and never saved there is lost like anywhere else, and
        // the handler gets the same warning as any other caller.
        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = Guid.NewGuid(),
            TraceId = Guid.NewGuid(),
        };

        try
        {
            await publisher.Publish(new SingleHandlerMessage());

            publisher.Dispose();

            sink.Warnings.Count.ShouldBe(1);
        }
        finally
        {
            JobExecutionContext.Current = null;
        }
    }

    [TimedFact]
    public async Task Dispose_InsideAHandlerScopeWhoseHandlerThrew_DoesNotWarn()
    {
        // A handler that staged a job and then threw: the job was rightly discarded with the failed
        // attempt, and telling the user to call SaveChangesAsync, on every retry, would be wrong.
        var sink = new WarningSink();
        await using var ctx = NewContext();
        var publisher = NewPublisher(ctx, sink, new WarpConfiguration());
        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = Guid.NewGuid(),
            TraceId = Guid.NewGuid(),
            HandlerFailed = true,
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
    public async Task ScopeEnds_WithUnsavedStagedJobs_LogsTheWarning()
    {
        // The real shape: the publisher is disposed BY its scope. A scope that is being disposed resolves
        // nothing, so a logger looked up inside Dispose was never found and the warning never logged.
        var sink = new WarningSink();
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddProvider(new CapturingLoggerProvider(sink)));
        services.AddScoped(_ => NewContext());
        services.AddScoped(x => new Publisher<TestContext>(x.GetRequiredService<TestContext>(), Options.Create(new WarpConfiguration()), TimeProvider.System, x, TestTasks.NullTransport, TestTasks.NullSignals));
        services.AddScoped(x => new BatchPublisher<TestContext>(x.GetRequiredService<TestContext>(), Options.Create(new WarpConfiguration()), TimeProvider.System, x, TestTasks.NullTransport, TestTasks.NullSignals));
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<Publisher<TestContext>>().Publish(new SingleHandlerMessage());
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<BatchPublisher<TestContext>>().StartNew([new SingleHandlerJob()]);
        }

        sink.Warnings.Count.ShouldBe(2);
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

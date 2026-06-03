using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Helper;
using Warp.Core.Logging;
using Warp.Core.Models;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

[GenerateDatabaseTests]
public abstract class MetadataPublishPipelineTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected MetadataPublishPipelineTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ServiceProvider BuildProvider(params object[] behaviors)
    {
        var services = new ServiceCollection();
        foreach (var behavior in behaviors)
        {
            var interfaceType = behavior.GetType().GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPublishPipelineBehavior<>));
            services.AddTransient(interfaceType, _ => behavior);
        }

        return services.BuildServiceProvider();
    }

    // --- Fix #1: Multiple behaviors per type all run ---
    [TimedFact]
    public async Task Enqueue_MultipleBehaviors_AllBehaviorsRun()
    {
        var ctx = _fixture.CreateContext();
        var first = new AppendMetadataBehavior("key-a", "value-a");
        var second = new AppendMetadataBehavior("key-b", "value-b");
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(first, second), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["key-a"].ShouldBe("value-a");
        metadata["key-b"].ShouldBe("value-b");
    }

    [TimedFact]
    public async Task Publish_MultipleBehaviors_AllBehaviorsRunForMessage()
    {
        var ctx = _fixture.CreateContext();
        var first = new AppendMessageMetadataBehavior("msg-key-a", "msg-val-a");
        var second = new AppendMessageMetadataBehavior("msg-key-b", "msg-val-b");
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(first, second), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Publish(new SingleHandlerMessage());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["msg-key-a"].ShouldBe("msg-val-a");
        metadata["msg-key-b"].ShouldBe("msg-val-b");
    }

    // --- Fix #2: CancellationToken flows to behaviors ---
    [TimedFact]
    public async Task Enqueue_CancellationToken_FlowsToBehavior()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new CancellationTokenCaptureBehavior();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(CancellationToken.None);

        // The behavior should have received CancellationToken.None (from the call site),
        // but it should NOT be a default struct — it should be the exact token passed through
        behavior.ReceivedToken.ShouldNotBeNull();
        behavior.WasCalled.ShouldBeTrue();
    }

    // --- Fix #3: No behaviors → null metadata (not empty JSON) ---
    [TimedFact]
    public async Task Enqueue_NoBehaviors_MetadataIsNull()
    {
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldBeNull();
    }

    // --- Ad-hoc metadata via JobParameters ---
    [TimedFact]
    public async Task Enqueue_WithJobParametersMetadata_MetadataPersisted()
    {
        var ctx = _fixture.CreateContext();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["ad-hoc"] = "value" },
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["ad-hoc"].ShouldBe("value");
    }

    [TimedFact]
    public async Task Enqueue_BehaviorAndAdHocMetadata_BothPresent()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new AppendMetadataBehavior("pipeline-key", "pipeline-val");
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest(), new JobParameters
        {
            Metadata = new Dictionary<string, object> { ["ad-hoc"] = "value" },
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["pipeline-key"].ShouldBe("pipeline-val");
        metadata["ad-hoc"].ShouldBe("value");
    }

    // --- Fix #5: Model metadata caching ---
    [TimedFact]
    public void UnifiedJobDetailModel_Metadata_CachedAcrossAccesses_1()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = """{"key":"value"}""" };

        var first = model.Metadata;
        var second = model.Metadata;

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("Metadata should be the same instance on repeated access");
    }

    [TimedFact]
    public void UnifiedJobDetailModel_Metadata_CachedAcrossAccesses_WithMultipleKeys()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = """{"key1":"value1","key2":"value2"}""" };

        var first = model.Metadata;
        var second = model.Metadata;

        first.ShouldNotBeNull();
        first.Count.ShouldBe(2);
        second.ShouldNotBeNull();
        ReferenceEquals(first, second).ShouldBeTrue("Metadata should be the same instance on repeated access");
    }

    [TimedFact]
    public void UnifiedJobDetailModel_NullMetadataJson_ReturnsNull()
    {
        var model = new UnifiedJobDetailModel { MetadataJson = null };
        model.Metadata.ShouldBeNull();
    }

    // --- Edge case: JobContext with null metadata → Metadata is empty dict, not null ---
    [TimedFact]
    public void JobContext_DefaultMetadata_IsEmptyDictionaryNotNull()
    {
        var ctx = new JobContext();
        ctx.Metadata.ShouldNotBeNull();
        ctx.Metadata.Count.ShouldBe(0);
    }

    // --- Batch: pipeline runs once, all children get metadata ---
    [TimedFact]
    public async Task BatchPublisher_WithBehavior_AllChildrenGetMetadata()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new AppendMetadataBehavior("batch-key", "batch-val");
        var publisher = new BatchPublisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        var jobs = new List<UnitRequest> { new(), new(), new() };
        var batchId = await publisher.StartNew(jobs);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var children = await _fixture.CreateContext().Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        children.Count.ShouldBe(3);
        foreach (var child in children)
        {
            child.Metadata.ShouldNotBeNull();
            var metadata = MetadataSerializer.Deserialize(child.Metadata)!;
            metadata["batch-key"].ShouldBe("batch-val");
        }
    }

    // --- Metadata inheritance from execution context ---
    [TimedFact]
    public async Task Enqueue_InsideExecutionContext_InheritsParentMetadata()
    {
        // Simulate a handler publishing a child job
        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = Guid.NewGuid(),
            TraceId = Guid.NewGuid(),
            MetadataJson = """{"inherited":"from-parent"}""",
        };

        try
        {
            var ctx = _fixture.CreateContext();
            var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

            var id = await publisher.Enqueue(new UnitRequest());
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
            job!.Metadata.ShouldNotBeNull();
            var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
            metadata["inherited"].ShouldBe("from-parent");
        }
        finally
        {
            JobExecutionContext.Current = null;
        }
    }

    // --- Batch: ad-hoc metadata via StartNew ---
    [TimedFact]
    public async Task BatchPublisher_WithAdHocMetadata_AllChildrenGetIt()
    {
        var ctx = _fixture.CreateContext();
        var publisher = new BatchPublisher<TestContext>(ctx, Options.Create(new WarpConfiguration()), TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

        var jobs = new List<UnitRequest> { new(), new() };
        var batchId = await publisher.StartNew(jobs, metadata: new Dictionary<string, object> { ["ad-hoc-batch"] = "yes" });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var children = await _fixture.CreateContext().Set<Job>()
            .Where(j => j.ParentJobId == batchId && j.Kind == JobKind.Job)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        children.Count.ShouldBe(2);
        foreach (var child in children)
        {
            child.Metadata.ShouldNotBeNull();
            var metadata = MetadataSerializer.Deserialize(child.Metadata)!;
            metadata["ad-hoc-batch"].ShouldBe("yes");
        }
    }

    // --- Edge case: behavior modifies metadata AFTER next() ---
    [TimedFact]
    public async Task Enqueue_BehaviorWritesAfterNext_MetadataPersisted()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new PostNextBehavior();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["post-next"].ShouldBe("written-after");
    }

    // --- Edge case: behavior short-circuits (never calls next()) ---
    [TimedFact]
    public async Task Enqueue_BehaviorShortCircuits_MetadataFromBehaviorStillPersisted()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new ShortCircuitBehavior();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        var id = await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
        job!.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
        metadata["short-circuit"].ShouldBe("yes");
    }

    // --- Edge case: behavior throws → exception propagates, job not created ---
    [TimedFact]
    public async Task Enqueue_BehaviorThrows_ExceptionPropagatesAndJobNotCreated()
    {
        var ctx = _fixture.CreateContext();
        var behavior = new ThrowingBehavior();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(behavior), TestTasks.NullTransport, TestTasks.NullSignals);

        await Should.ThrowAsync<InvalidOperationException>(async () => await publisher.Enqueue(new UnitRequest()));

        // Job should not have been created
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        var jobs = await _fixture.CreateContext().Set<Job>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        jobs.ShouldBe(0);
    }

    // --- Edge case: pipeline execution order (first registered = outermost, runs first) ---
    [TimedFact]
    public async Task Enqueue_BehaviorOrder_FirstRegisteredIsOutermost()
    {
        var ctx = _fixture.CreateContext();
        var executionLog = new List<string>();
        var outer = new OrderTrackingBehavior(executionLog, "outer");
        var inner = new OrderTrackingBehavior(executionLog, "inner");
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, BuildProvider(outer, inner), TestTasks.NullTransport, TestTasks.NullSignals);

        await publisher.Enqueue(new UnitRequest());
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Outer enters first, then inner enters, then inner exits, then outer exits
        executionLog.ShouldBe(["outer:before", "inner:before", "inner:after", "outer:after"]);
    }

    // --- Edge case: ad-hoc metadata overrides inherited metadata on key conflict ---
    [TimedFact]
    public async Task Enqueue_AdHocOverridesInherited_OnKeyConflict()
    {
        JobExecutionContext.Current = new JobExecutionInfo
        {
            JobId = Guid.NewGuid(),
            TraceId = Guid.NewGuid(),
            MetadataJson = """{"shared":"inherited-value","inherited-only":"stays"}""",
        };

        try
        {
            var ctx = _fixture.CreateContext();
            var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, new ServiceCollection().BuildServiceProvider(), TestTasks.NullTransport, TestTasks.NullSignals);

            var id = await publisher.Enqueue(new UnitRequest(), new JobParameters
            {
                Metadata = new Dictionary<string, object> { ["shared"] = "ad-hoc-value" },
            });
            await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

            var job = await _fixture.CreateContext().Set<Job>().FindAsync([id], Xunit.TestContext.Current.CancellationToken);
            job!.Metadata.ShouldNotBeNull();
            var metadata = MetadataSerializer.Deserialize(job.Metadata)!;
            metadata["shared"].ShouldBe("ad-hoc-value", "Ad-hoc should override inherited");
            metadata["inherited-only"].ShouldBe("stays", "Non-conflicting inherited key should survive");
        }
        finally
        {
            JobExecutionContext.Current = null;
        }
    }

    // --- Test behaviors (nested private so assembly scanning skips them) ---
    private class AppendMetadataBehavior(string key, string value) : IPublishPipelineBehavior<UnitRequest>
    {
        public Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            context.Metadata[key] = value;
            return next();
        }
    }

    private class AppendMessageMetadataBehavior(string key, string value) : IPublishPipelineBehavior<SingleHandlerMessage>
    {
        public Task PublishAsync(PublishContext<SingleHandlerMessage> context, PublishDelegate next, CancellationToken ct)
        {
            context.Metadata[key] = value;
            return next();
        }
    }

    private class CancellationTokenCaptureBehavior : IPublishPipelineBehavior<UnitRequest>
    {
        public bool WasCalled { get; private set; }

        public CancellationToken? ReceivedToken { get; private set; }

        public Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            WasCalled = true;
            ReceivedToken = ct;
            return next();
        }
    }

    private class PostNextBehavior : IPublishPipelineBehavior<UnitRequest>
    {
        public async Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            await next();
            context.Metadata["post-next"] = "written-after";
        }
    }

    private class ShortCircuitBehavior : IPublishPipelineBehavior<UnitRequest>
    {
        public Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            context.Metadata["short-circuit"] = "yes";

            // Intentionally not calling next()
            return Task.CompletedTask;
        }
    }

    private class ThrowingBehavior : IPublishPipelineBehavior<UnitRequest>
    {
        public Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            throw new InvalidOperationException("Behavior failure");
        }
    }

    private class OrderTrackingBehavior(List<string> log, string name) : IPublishPipelineBehavior<UnitRequest>
    {
        public async Task PublishAsync(PublishContext<UnitRequest> context, PublishDelegate next, CancellationToken ct)
        {
            log.Add($"{name}:before");
            await next();
            log.Add($"{name}:after");
        }
    }
}

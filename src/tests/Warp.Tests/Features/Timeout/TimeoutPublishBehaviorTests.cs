using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core.Handlers;
using Warp.Core.Timeout;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.Timeout;

[Trait("Category", "NoDb")]
public class TimeoutPublishBehaviorTests
{
    private static readonly DateTimeOffset PublishMoment = new(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(ITimeoutMetadata Meta, FakeTimeProvider Time)> RunBehavior<T>(
        T job,
        Dictionary<string, object>? startingMetadata = null,
        TimeoutOptions? options = null)
    {
        var time = new FakeTimeProvider(PublishMoment);
        var behavior = new TimeoutPublishBehavior<T>(
            Options.Create(options ?? new TimeoutOptions()),
            time);

        var context = new PublishContext<T>
        {
            Job = job!,
            Metadata = startingMetadata ?? [],
        };

        await behavior.PublishAsync(context, () => Task.CompletedTask, CancellationToken.None);

        return (context.GetMetadata<ITimeoutMetadata>(), time);
    }

    [TimedFact]
    public async Task NoAttribute_NoDefault_MetadataUnchanged()
    {
        var (meta, _) = await RunBehavior(new UnitRequest());

        meta.TimeoutSeconds.ShouldBeNull();
        meta.TimeoutMode.ShouldBeNull();
        meta.TimeoutScope.ShouldBeNull();
        meta.TimeoutDeadlineUtc.ShouldBeNull();
    }

    [TimedFact]
    public async Task Attribute_AppliesSecondsAndDefaults()
    {
        var (meta, _) = await RunBehavior(new TimeoutAttributeRequest());

        meta.TimeoutSeconds.ShouldBe(30);
        meta.TimeoutMode.ShouldBe(TimeoutMode.Delete);
        meta.TimeoutScope.ShouldBe(TimeoutScope.PerAttempt);
        meta.TimeoutDeadlineUtc.ShouldBeNull();
    }

    [TimedFact]
    public async Task FailModeAttribute_PropagatesMode()
    {
        var (meta, _) = await RunBehavior(new TimeoutFailModeRequest());

        meta.TimeoutSeconds.ShouldBe(60);
        meta.TimeoutMode.ShouldBe(TimeoutMode.Fail);
    }

    [TimedFact]
    public async Task TotalScopeAttribute_StampsDeadlineAtPublishTime()
    {
        var (meta, _) = await RunBehavior(new TimeoutTotalScopeRequest());

        meta.TimeoutScope.ShouldBe(TimeoutScope.Total);
        meta.TimeoutDeadlineUtc.ShouldNotBeNull();
        meta.TimeoutDeadlineUtc.Value.ShouldBe(PublishMoment.UtcDateTime.AddSeconds(60));
    }

    [TimedFact]
    public async Task PerAttemptScope_DeadlineRemainsNull()
    {
        var (meta, _) = await RunBehavior(new TimeoutAttributeRequest());

        meta.TimeoutScope.ShouldBe(TimeoutScope.PerAttempt);
        meta.TimeoutDeadlineUtc.ShouldBeNull();
    }

    [TimedFact]
    public async Task WithTimeoutMetadataPreset_AttributeIgnored()
    {
        var starting = new Dictionary<string, object> { ["TimeoutSeconds"] = 5 };

        var (meta, _) = await RunBehavior(new TimeoutAttributeRequest(), starting);

        meta.TimeoutSeconds.ShouldBe(5);
    }

    [TimedFact]
    public async Task NoAttribute_TotalScopedDefault_StampedWithDeadline()
    {
        // Only the Total-scoped default keeps publish stamping: its deadline measures from enqueue
        // and must exist before the first execution (§8.31 attainment reads it pre-execution).
        var options = new TimeoutOptions
        {
            Default = TimeSpan.FromSeconds(45),
            DefaultMode = TimeoutMode.Fail,
            DefaultScope = TimeoutScope.Total,
        };

        var (meta, _) = await RunBehavior(new UnitRequest(), options: options);

        meta.TimeoutSeconds.ShouldBe(45);
        meta.TimeoutMode.ShouldBe(TimeoutMode.Fail);
        meta.TimeoutScope.ShouldBe(TimeoutScope.Total);
        meta.TimeoutDeadlineUtc.ShouldBe(PublishMoment.UtcDateTime.AddSeconds(45));
    }

    [TimedFact]
    public async Task NoAttribute_PerAttemptDefault_NotStampedAtPublish()
    {
        // The PerAttempt default moved to execution (TimeoutPipelineBehavior): stamping it here
        // filled the metadata slot for every job and made a handler-declared [Timeout] unreachable —
        // the same shadowing Retry fixed as #236. Same effective timeout, applied per attempt from
        // live options; the value no longer appears in Job.Metadata at enqueue.
        var options = new TimeoutOptions { Default = TimeSpan.FromSeconds(45) };

        var (meta, _) = await RunBehavior(new UnitRequest(), options: options);

        meta.TimeoutSeconds.ShouldBeNull();
        meta.TimeoutMode.ShouldBeNull();
        meta.TimeoutScope.ShouldBeNull();
        meta.TimeoutDeadlineUtc.ShouldBeNull();
    }

    [TimedFact]
    public async Task AttributeBeatsDefault_WhenBothConfigured()
    {
        var options = new TimeoutOptions { Default = TimeSpan.FromSeconds(45) };

        var (meta, _) = await RunBehavior(new TimeoutAttributeRequest(), options: options);

        meta.TimeoutSeconds.ShouldBe(30);
    }

    [TimedFact]
    public async Task DerivedTypeWithoutAttribute_DoesNotInheritBaseAttribute()
    {
        // [Timeout] is declared [AttributeUsage(Inherited = false)]. A derived class with no
        // [Timeout] of its own should NOT pick up the base's attribute. This pins the explicit
        // intent — operators must annotate each concrete job type. If a future change toggles
        // Inherited to true (intentionally or not), this test fails loudly.
        var (meta, _) = await RunBehavior(new TimeoutDerivedWithoutAttributeRequest());

        meta.TimeoutSeconds.ShouldBeNull();
        meta.TimeoutMode.ShouldBeNull();
        meta.TimeoutScope.ShouldBeNull();
    }

    [TimedFact]
    public async Task TotalScope_DeadlineSurvivesSerializationRoundtrip()
    {
        // After publish, the worker round-trips metadata through MetadataSerializer (JSON +
        // NativeObjectConverter). DateTime values are written as ISO 8601 strings and come
        // back as strings — MetadataConvert must restore them to DateTime, or Total scope
        // silently degrades to PerAttempt after the first attempt.
        var time = new FakeTimeProvider(PublishMoment);
        var behavior = new TimeoutPublishBehavior<TimeoutTotalScopeRequest>(
            Options.Create(new TimeoutOptions()),
            time);

        var context = new PublishContext<TimeoutTotalScopeRequest>
        {
            Job = new TimeoutTotalScopeRequest(),
            Metadata = [],
        };

        await behavior.PublishAsync(context, () => Task.CompletedTask, CancellationToken.None);

        var serialized = MetadataSerializer.Serialize(context.Metadata);
        var deserialized = MetadataSerializer.Deserialize(serialized);
        var roundtripped = MetadataFactory.Create<ITimeoutMetadata>(deserialized);

        roundtripped.TimeoutDeadlineUtc.ShouldNotBeNull();
        roundtripped.TimeoutDeadlineUtc!.Value.ShouldBe(PublishMoment.UtcDateTime.AddSeconds(60));
        roundtripped.TimeoutMode.ShouldBe(TimeoutMode.Fail);
        roundtripped.TimeoutScope.ShouldBe(TimeoutScope.Total);
        roundtripped.TimeoutSeconds.ShouldBe(60);
    }

    [TimedFact]
    public async Task CallsNext()
    {
        var called = false;
        var behavior = new TimeoutPublishBehavior<UnitRequest>(
            Options.Create(new TimeoutOptions()),
            new FakeTimeProvider(PublishMoment));
        var context = new PublishContext<UnitRequest>
        {
            Job = new UnitRequest(),
            Metadata = [],
        };

        await behavior.PublishAsync(
            context,
            () =>
            {
                called = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        called.ShouldBeTrue();
    }
}

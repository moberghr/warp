using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
using Warp.Core.Notifiers;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// The engine (§8.29): <see cref="ErrorGroupAggregator{TContext}"/> drains the <see cref="ErrorOccurrence"/>
/// inbox into durable <see cref="ErrorGroup"/> issues off the hot path — grouping by fingerprint, folding the
/// hourly trend Counter, draining exactly-once, and re-opening a resolved group on a later occurrence. Both
/// providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ErrorGroupAggregatorTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ErrorGroupAggregatorTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private const string NreStack =
        "System.NullReferenceException: boom\n   at Acme.Orders.ProcessOrderHandler.Handle(Cmd c) in P.cs:line 42";

    // The fingerprint the JobNre occurrence resolves to: hash(Job, type, top-in-app-frame). The frame in NreStack
    // (Acme.Orders.ProcessOrderHandler.Handle) isn't framework/plumbing, so it — not the culprit — is the locus.
    private static readonly string NreFingerprint =
        ErrorFingerprint.Compute(ErrorSource.Job, "System.NullReferenceException", "Acme.Orders.ProcessOrderHandler.Handle");

    private ErrorGroupAggregator<TestContext> Aggregator(
        TimeSpan? interval = null,
        bool captureErrorSamples = true,
        int? maxDistinctErrorGroups = null,
        WarpNotifierDispatcher? dispatcher = null)
    {
        var config = new WarpServerConfiguration
        {
            ErrorGroupingInterval = interval ?? TimeSpan.FromSeconds(15),
            CaptureErrorSamples = captureErrorSamples,
        };

        if (maxDistinctErrorGroups is { } cap)
        {
            config.MaxDistinctErrorGroups = cap;
        }

        return new(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(config),
            TimeProvider.System,
            dispatcher ?? TestNotifiers.EmptyDispatcher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ErrorGroupAggregator<TestContext>>.Instance);
    }

    // A distinct-fingerprint Job (or Client) error: no stack, so the aggregator's locus falls back to Culprit
    // — a different culprit ⇒ a different fingerprint ⇒ a different issue. Used to drive the cardinality guard.
    private static ErrorOccurrence SourceError(ErrorSource source, string culprit, DateTime at)
        => new()
        {
            Source = source,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.InvalidOperationException",
            Message = $"failure in {culprit}",
            Stack = null,
            Culprit = culprit,
            Timestamp = at,
        };

    private static ErrorOccurrence JobNre(string message, DateTime at, string? version = null, string? environment = null)
        => new()
        {
            Source = ErrorSource.Job,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.NullReferenceException",
            Message = message,
            Stack = NreStack,
            Culprit = "Acme.Orders.ProcessOrderRequest",
            Application = "worker",
            Version = version,
            Environment = environment,
            Timestamp = at,
        };

    [TimedFact]
    public async Task Aggregator_GroupsByFingerprintAndDrainsInbox()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();

        // Two occurrences of the same error (message-varying) + one different-typed error.
        ctx.Set<ErrorOccurrence>().Add(JobNre("Order 12345 failed", basis));
        ctx.Set<ErrorOccurrence>().Add(JobNre("Order 67890 failed", basis.AddSeconds(1)));
        ctx.Set<ErrorOccurrence>().Add(new ErrorOccurrence
        {
            Source = ErrorSource.Job,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.TimeoutException",
            Message = "timed out",
            Stack = "System.TimeoutException: x\n   at Acme.Reports.GenerateReportHandler.Handle()",
            Culprit = "Acme.Reports.GenerateReportRequest",
            Timestamp = basis.AddSeconds(2),
        });
        await ctx.SaveChangesAsync(Ct);

        await Aggregator().ExecuteAsync(Ct);

        var read = _fixture.CreateContext();
        var groups = await read.Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct);
        groups.Count.ShouldBe(2);

        var nre = groups
            .Where(x => string.Equals(x.ExceptionType, "System.NullReferenceException", StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        nre.Count.ShouldBe(2);                                  // both occurrences folded into one issue
        nre.Title.ShouldBe("Order <num> failed");               // normalized (message-varying collapsed)
        nre.Status.ShouldBe(ErrorGroupStatus.Unresolved);
        nre.LastSample.ShouldNotBeNull();

        // Inbox fully drained; hourly trend Counter written (survives raw-row cleanup).
        (await read.Set<ErrorOccurrence>().CountAsync(Ct)).ShouldBe(0);
        (await read.Set<Counter>().Where(x => x.Key.StartsWith("errorgroup:")).CountAsync(Ct)).ShouldBeGreaterThan(0);
    }

    [TimedFact]
    public async Task Aggregator_DrainIsExactlyOnce_AcrossTwoRuns()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorOccurrence>().Add(JobNre("boom", basis));
        await ctx.SaveChangesAsync(Ct);

        await Aggregator().ExecuteAsync(Ct);
        await Aggregator().ExecuteAsync(Ct);   // nothing left to drain

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Count.ShouldBe(1);               // not double-counted
    }

    [TimedFact]
    public async Task Aggregator_ResolvedGroup_ReopensOnLaterOccurrence()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var fingerprint = ErrorFingerprint.Compute(ErrorSource.Job, "System.NullReferenceException", "Acme.Orders.ProcessOrderHandler.Handle");

        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(new ErrorGroup
        {
            Fingerprint = fingerprint,
            Source = ErrorSource.Job,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.NullReferenceException",
            Title = "boom",
            Culprit = "Acme.Orders.ProcessOrderRequest",
            FirstSeenAt = basis,
            LastSeenAt = basis,
            Count = 5,
            Status = ErrorGroupStatus.Resolved,
            StatusChangedAt = basis.AddMinutes(1),
        });

        // An occurrence AFTER the resolve instant → regression.
        ctx.Set<ErrorOccurrence>().Add(JobNre("again", basis.AddMinutes(2)));
        await ctx.SaveChangesAsync(Ct);

        await Aggregator().ExecuteAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Status.ShouldBe(ErrorGroupStatus.Unresolved);    // re-opened
        group.Count.ShouldBe(6);
    }

    [TimedFact]
    public async Task Aggregator_StampsVersionAndEnvironment_OnInsert()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorOccurrence>().Add(JobNre("boom", basis, version: "1.4.2", environment: "prod"));
        await ctx.SaveChangesAsync(Ct);

        await Aggregator().ExecuteAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.FirstSeenVersion.ShouldBe("1.4.2");
        group.LastSeenVersion.ShouldBe("1.4.2");
        group.Environment.ShouldBe("prod");
        group.RecentSamples.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task Aggregator_LaterOccurrence_UpdatesLastSeenVersion_NotFirstSeen()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        var first = _fixture.CreateContext();
        first.Set<ErrorOccurrence>().Add(JobNre("boom", basis, version: "1.4.2", environment: "prod"));
        await first.SaveChangesAsync(Ct);
        await Aggregator().ExecuteAsync(Ct);

        var second = _fixture.CreateContext();
        second.Set<ErrorOccurrence>().Add(JobNre("boom again", basis.AddMinutes(5), version: "1.5.0", environment: "prod"));
        await second.SaveChangesAsync(Ct);
        await Aggregator().ExecuteAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.FirstSeenVersion.ShouldBe("1.4.2");        // unchanged
        group.LastSeenVersion.ShouldBe("1.5.0");         // advanced
        group.Environment.ShouldBe("prod");
    }

    [TimedFact]
    public async Task Aggregator_RecentSamples_NewestFirst_CappedAtTen_AcrossFolds()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        // Fold A: occurrences 0..5, then fold B: 6..11 — 12 total, prepended newest-first, re-capped to 10.
        await FoldRange(basis, 0, 6);
        await FoldRange(basis, 6, 12);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.RecentSamples.ShouldNotBeNull();

        using var doc = JsonDocument.Parse(group.RecentSamples!);
        var arr = doc.RootElement;
        arr.GetArrayLength().ShouldBe(10);                                   // capped
        arr[0].GetProperty("message").GetString().ShouldBe("occ-11");        // newest first
        arr[9].GetProperty("message").GetString().ShouldBe("occ-2");         // oldest retained
    }

    [TimedFact]
    public async Task Aggregator_CaptureErrorSamplesOff_LeavesRecentSamplesNull()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorOccurrence>().Add(JobNre("boom", basis, version: "1.4.2", environment: "prod"));
        await ctx.SaveChangesAsync(Ct);

        await Aggregator(captureErrorSamples: false).ExecuteAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.RecentSamples.ShouldBeNull();
        group.LastSample.ShouldBeNull();
        group.FirstSeenVersion.ShouldBe("1.4.2");   // version stamping is independent of sample capture
    }

    [TimedFact]
    public async Task Aggregator_PastCardinalityCap_CollapsesNewFingerprintsIntoOtherBucket_PerSource()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        var ctx = _fixture.CreateContext();

        // ONE source (Job) floods PAST the cap: 3 distinct culprits, cap 2 ⇒ first two become real issues, the
        // third collapses into the per-source {other} bucket. A DIFFERENT source (Client) gets its full cap so we
        // can prove flooding Job didn't consume Client's capacity (per-source isolation).
        ctx.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Job, "Acme.A", basis));
        ctx.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Job, "Acme.B", basis.AddSeconds(1)));
        ctx.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Job, "Acme.C", basis.AddSeconds(2)));   // overflow
        ctx.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Client, "Web.X", basis.AddSeconds(3)));
        ctx.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Client, "Web.Y", basis.AddSeconds(4)));
        await ctx.SaveChangesAsync(Ct);

        await Aggregator(maxDistinctErrorGroups: 2).ExecuteAsync(Ct);

        var read = _fixture.CreateContext();
        var jobGroups = await read.Set<ErrorGroup>().AsNoTracking().Where(x => x.Source == ErrorSource.Job).ToListAsync(Ct);
        var clientGroups = await read.Set<ErrorGroup>().AsNoTracking().Where(x => x.Source == ErrorSource.Client).ToListAsync(Ct);

        // Job: exactly `cap` real issues + one {other}.
        var jobReal = jobGroups.Where(x => !string.Equals(x.ExceptionType, "{other}", StringComparison.Ordinal)).ToList();
        jobReal.Count.ShouldBe(2);
        var jobOther = jobGroups.Where(x => string.Equals(x.ExceptionType, "{other}", StringComparison.Ordinal)).ShouldHaveSingleItem();
        jobOther.Count.ShouldBe(1);                    // the one overflow occurrence
        jobOther.LastSample.ShouldNotBeNull();         // {other} still carries a debugging sample

        // Client is unaffected: it got its full cap of real issues (Job's overflow did NOT count against Client).
        clientGroups.Count.ShouldBe(2);
        clientGroups.Count(x => string.Equals(x.ExceptionType, "{other}", StringComparison.Ordinal)).ShouldBe(0);

        // A repeat run with a 4th distinct Job culprit keeps folding overflow into the SAME single {other}.
        var more = _fixture.CreateContext();
        more.Set<ErrorOccurrence>().Add(SourceError(ErrorSource.Job, "Acme.D", basis.AddSeconds(10)));
        await more.SaveChangesAsync(Ct);

        await Aggregator(maxDistinctErrorGroups: 2).ExecuteAsync(Ct);

        var afterRead = _fixture.CreateContext();
        var jobOtherAfter = (await afterRead.Set<ErrorGroup>()
                .AsNoTracking()
                .Where(x => x.Source == ErrorSource.Job)
                .Where(x => x.ExceptionType == "{other}")
                .ToListAsync(Ct))
            .ShouldHaveSingleItem();
        jobOtherAfter.Count.ShouldBe(2);               // folded, not a second {other}
        (await afterRead.Set<ErrorGroup>().AsNoTracking().CountAsync(x => x.Source == ErrorSource.Job, Ct)).ShouldBe(3);   // 2 real + 1 {other}
    }

    [TimedFact]
    public async Task Aggregator_ResolvedGroupRegresses_DispatchesIssueRegressedEvent_PostCommit()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(NreGroup(basis, basis, ErrorGroupStatus.Resolved, statusChangedAt: basis.AddMinutes(1)));
        ctx.Set<ErrorOccurrence>().Add(JobNre("again", basis.AddMinutes(2)));   // AFTER the resolve instant ⇒ regression
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        var aggregator = Aggregator(dispatcher: TestNotifiers.SpyDispatcher(spy));

        await aggregator.ExecuteAsync(Ct);
        spy.Received.ShouldBeEmpty("nothing dispatches until the post-commit hook");

        await aggregator.OnCommittedAsync(Ct);

        var evt = spy.Received.ShouldHaveSingleItem().ShouldBeOfType<IssueRegressedEvent>();
        evt.Fingerprint.ShouldBe(NreFingerprint);
        evt.Source.ShouldBe(ErrorSource.Job);
        evt.ExceptionType.ShouldBe("System.NullReferenceException");
        evt.Culprit.ShouldBe("Acme.Orders.ProcessOrderRequest");
        evt.Severity.ShouldBe(WarpEventSeverity.Warning);
    }

    [TimedFact]
    public async Task Aggregator_ResolvedGroup_PreResolveBacklog_StaysResolved_NoRegression()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        // Resolved at basis+5; the occurrence is at basis (<= StatusChangedAt) — pre-resolve backlog, not a regression.
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(NreGroup(basis, basis, ErrorGroupStatus.Resolved, statusChangedAt: basis.AddMinutes(5)));
        ctx.Set<ErrorOccurrence>().Add(JobNre("late-arriving backlog", basis));
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        var aggregator = Aggregator(dispatcher: TestNotifiers.SpyDispatcher(spy));

        await aggregator.ExecuteAsync(Ct);
        await aggregator.OnCommittedAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Status.ShouldBe(ErrorGroupStatus.Resolved);   // stays resolved
        group.Count.ShouldBe(6);                            // count still folds
        spy.Received.ShouldBeEmpty();                       // no regression event
    }

    [TimedFact]
    public async Task Aggregator_IgnoredGroup_LaterOccurrence_StaysIgnored_NoRegression()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        // Ignored is a deliberate mute: a later occurrence still counts but never re-opens the group.
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(NreGroup(basis, basis, ErrorGroupStatus.Ignored, statusChangedAt: basis, count: 3));
        ctx.Set<ErrorOccurrence>().Add(JobNre("still happening", basis.AddMinutes(2)));
        await ctx.SaveChangesAsync(Ct);

        var spy = new SpyNotifier();
        var aggregator = Aggregator(dispatcher: TestNotifiers.SpyDispatcher(spy));

        await aggregator.ExecuteAsync(Ct);
        await aggregator.OnCommittedAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Status.ShouldBe(ErrorGroupStatus.Ignored);   // stays ignored
        group.Count.ShouldBe(4);                           // count still increments
        spy.Received.ShouldBeEmpty();                      // no regression event
    }

    [TimedFact]
    public async Task Aggregator_CorruptRecentSamples_DoesNotThrow_AndRebuildsValidJson()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);

        // A pre-existing group with a corrupt RecentSamples payload — a bad parse must not abort the whole drain.
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(NreGroup(basis, basis, ErrorGroupStatus.Unresolved, statusChangedAt: null, count: 1, recentSamples: "{not json"));
        ctx.Set<ErrorOccurrence>().Add(JobNre("fresh-sample", basis.AddMinutes(1)));
        await ctx.SaveChangesAsync(Ct);

        // The corrupt prior history is dropped (treated as empty), never propagated or thrown.
        await Aggregator().ExecuteAsync(Ct);

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Count.ShouldBe(2);
        group.RecentSamples.ShouldNotBeNull();

        using var doc = JsonDocument.Parse(group.RecentSamples!);   // valid JSON again
        var arr = doc.RootElement;
        arr.GetArrayLength().ShouldBe(1);                           // only the new sample; corrupt history gone
        arr[0].GetProperty("message").GetString().ShouldBe("fresh-sample");
    }

    private static ErrorGroup NreGroup(
        DateTime firstSeen,
        DateTime lastSeen,
        ErrorGroupStatus status,
        DateTime? statusChangedAt,
        long count = 5,
        string? recentSamples = null)
        => new()
        {
            Fingerprint = NreFingerprint,
            Source = ErrorSource.Job,
            Kind = ErrorKind.Exception,
            ExceptionType = "System.NullReferenceException",
            Title = "boom",
            Culprit = "Acme.Orders.ProcessOrderRequest",
            FirstSeenAt = firstSeen,
            LastSeenAt = lastSeen,
            Count = count,
            Status = status,
            StatusChangedAt = statusChangedAt,
            RecentSamples = recentSamples,
        };

    private async Task FoldRange(DateTime basis, int startInclusive, int endExclusive)
    {
        var ctx = _fixture.CreateContext();
        for (var i = startInclusive; i < endExclusive; i++)
        {
            ctx.Set<ErrorOccurrence>().Add(JobNre($"occ-{i}", basis.AddSeconds(i)));
        }

        await ctx.SaveChangesAsync(Ct);
        await Aggregator().ExecuteAsync(Ct);
    }
}

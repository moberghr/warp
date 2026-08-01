using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;
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

    private ErrorGroupAggregator<TestContext> Aggregator(TimeSpan? interval = null, bool captureErrorSamples = true)
        => new(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration { ErrorGroupingInterval = interval ?? TimeSpan.FromSeconds(15), CaptureErrorSamples = captureErrorSamples }),
            TimeProvider.System,
            TestNotifiers.EmptyDispatcher());

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

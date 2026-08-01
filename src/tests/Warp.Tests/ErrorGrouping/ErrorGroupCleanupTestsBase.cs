using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// <c>ExpirationCleanup</c> coverage for the error grouping tables (§8.29): expired <see cref="ErrorGroup"/> rows
/// are deleted past their stamped <c>ExpireAt</c>; a global count cap keeps the newest N by <c>LastSeenAt</c>; and
/// a defensive sweep deletes <see cref="ErrorOccurrence"/> inbox rows the aggregator left behind (older than 1h).
/// Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ErrorGroupCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ErrorGroupCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task CleanupExpired_DeletesOnlyExpiredGroups()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().AddRange(
            Group("fp-expired", expireAt: DateTime.UtcNow.AddHours(-1)),
            Group("fp-future", expireAt: DateTime.UtcNow.AddHours(1)),
            Group("fp-never", expireAt: null));
        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup().CleanupExpiredErrorGroupsAsync(Ct);

        deleted.ShouldBe(1);
        var remaining = await _fixture.CreateContext().Set<ErrorGroup>().Select(x => x.Fingerprint).ToListAsync(Ct);
        remaining.ShouldBe(["fp-future", "fp-never"], ignoreOrder: true);
    }

    [TimedFact]
    public async Task CleanupByCount_KeepsNewestNByLastSeen()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            var g = Group($"fp-{i}", expireAt: null);
            g.LastSeenAt = basis.AddMinutes(i);   // fp-4 is newest
            ctx.Set<ErrorGroup>().Add(g);
        }

        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup(retentionCount: 2).CleanupErrorGroupsByCountAsync(Ct);

        deleted.ShouldBe(3);
        var remaining = await _fixture.CreateContext().Set<ErrorGroup>().Select(x => x.Fingerprint).ToListAsync(Ct);
        remaining.ShouldBe(["fp-3", "fp-4"], ignoreOrder: true);   // the two newest kept
    }

    [TimedFact]
    public async Task CleanupByCount_NoCap_KeepsAll()
    {
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            ctx.Set<ErrorGroup>().Add(Group($"fp-{i}", expireAt: null));
        }

        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup(retentionCount: null).CleanupErrorGroupsByCountAsync(Ct);

        deleted.ShouldBe(0);
        (await _fixture.CreateContext().Set<ErrorGroup>().CountAsync(Ct)).ShouldBe(3);
    }

    [TimedFact]
    public async Task CleanupOrphanOccurrences_DeletesOnlyOldRows()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorOccurrence>().AddRange(
            Occurrence(DateTime.UtcNow.AddHours(-2)),   // stale — aggregator left it
            Occurrence(DateTime.UtcNow.AddMinutes(-5))); // recent — normal inbox row
        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup().CleanupOrphanErrorOccurrencesAsync(Ct);

        deleted.ShouldBe(1);
        (await _fixture.CreateContext().Set<ErrorOccurrence>().CountAsync(Ct)).ShouldBe(1);
    }

    private ExpirationCleanup<TestContext> CreateCleanup(int? retentionCount = null)
    {
        var configuration = new WarpServerConfiguration { ErrorGroupRetentionCount = retentionCount };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration),
            TestNotifiers.EmptyDispatcher());
    }

    private static ErrorGroup Group(string fingerprint, DateTime? expireAt) => new()
    {
        Fingerprint = fingerprint,
        Source = ErrorSource.Job,
        Kind = ErrorKind.Exception,
        ExceptionType = "System.NullReferenceException",
        Title = "boom",
        Culprit = "Acme.Orders.ProcessOrderRequest",
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        Count = 1,
        Status = ErrorGroupStatus.Unresolved,
        ExpireAt = expireAt,
    };

    private static ErrorOccurrence Occurrence(DateTime timestamp) => new()
    {
        Source = ErrorSource.Job,
        Kind = ErrorKind.Exception,
        ExceptionType = "System.NullReferenceException",
        Culprit = "Acme.Orders.ProcessOrderRequest",
        Timestamp = timestamp,
    };
}

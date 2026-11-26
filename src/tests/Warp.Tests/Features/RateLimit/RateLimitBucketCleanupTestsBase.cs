using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Features.RateLimit;

/// <summary>
/// <c>ExpirationCleanup</c> deletes <see cref="RateLimitBucket"/> rows idle for longer than the longest job
/// window (7 days) plus a day, so a per-tenant or per-user key does not leave a row forever. A bucket still
/// inside any possible window, one holding a future Wait-mode cursor, and an adapter's bucket all stay.
/// </summary>
[GenerateDatabaseTests]
public abstract class RateLimitBucketCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RateLimitBucketCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task CleanupStaleRateLimitBuckets_DeletesOnlyBucketsIdlePastTheLongestWindow()
    {
        var now = DateTime.UtcNow;
        var ctx = _fixture.CreateContext();
        ctx.Set<RateLimitBucket>().AddRange(
            Bucket("tenant-stale", updatedAt: now.AddDays(-9)),
            Bucket("tenant-stale-past-cursor", updatedAt: now.AddDays(-9), nextSlotUtc: now.AddDays(-8)),
            Bucket("tenant-in-window", updatedAt: now.AddDays(-7)),
            Bucket("tenant-recent", updatedAt: now.AddMinutes(-1)),
            Bucket("tenant-future-cursor", updatedAt: now.AddDays(-9), nextSlotUtc: now.AddMinutes(5)),
            Bucket("warp:adapter:vendor", updatedAt: now.AddDays(-30)));
        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup().CleanupStaleRateLimitBucketsAsync(Ct);

        deleted.ShouldBe(2);
        var remaining = await _fixture.CreateContext().Set<RateLimitBucket>().Select(x => x.Name).ToListAsync(Ct);
        remaining.ShouldBe(["tenant-in-window", "tenant-recent", "tenant-future-cursor", "warp:adapter:vendor"], ignoreOrder: true);
    }

    [TimedFact]
    public async Task CleanupStaleRateLimitBuckets_MoreThanOneBatch_DeletesAll()
    {
        var now = DateTime.UtcNow;
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 5; i++)
        {
            ctx.Set<RateLimitBucket>().Add(Bucket($"tenant-{i}", updatedAt: now.AddDays(-10)));
        }

        await ctx.SaveChangesAsync(Ct);

        var deleted = await CreateCleanup(batchSize: 2).CleanupStaleRateLimitBucketsAsync(Ct);

        deleted.ShouldBe(5);
        (await _fixture.CreateContext().Set<RateLimitBucket>().CountAsync(Ct)).ShouldBe(0);
    }

    private static RateLimitBucket Bucket(string name, DateTime updatedAt, DateTime? nextSlotUtc = null) => new()
    {
        Name = name,
        WindowStartUtc = updatedAt,
        CurrentCount = 1,
        UpdatedAt = updatedAt,
        NextSlotUtc = nextSlotUtc,
    };

    private ExpirationCleanup<TestContext> CreateCleanup(int batchSize = 1000)
    {
        var configuration = new WarpServerConfiguration { ExpirationBatchSize = batchSize };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration),
            TestNotifiers.EmptyDispatcher());
    }
}

using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Concurrency coverage for the atomic attempt claim (BUG-3). Two executor jobs for one delivery (a
/// stale-lease re-enqueue) both read the same <c>(Pending, AttemptCount)</c> and would each POST and race
/// <c>AttemptCount</c>. <see cref="ExecuteWebhookDeliveryHandler{TContext}.TryClaimAttemptAsync"/> is the
/// guard, and it runs <b>before</b> the HTTP leg, so a lost claim never POSTs. The claim primitive is tested
/// directly (§4.8) — the deterministic invariant is "exactly one of N racing claims for the same loaded
/// attempt wins", which is what proves the double-POST is impossible.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookAttemptClaimTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WebhookAttemptClaimTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task TryClaimAttempt_TwoConcurrentClaimsForSameAttempt_ExactlyOneWins()
    {
        var id = await SeedAsync(WebhookDeliveryStatus.Pending, attemptCount: 0);

        // Pin both claims at the ExecuteUpdate (BarrierSignal-style, N=2): each opens its own context, signals
        // it is poised, then both are released together to race the guarded atomic increment against the same
        // loaded AttemptCount == 0. The DB serialises the two ExecuteUpdates — exactly one matches the row.
        var poised = new SemaphoreSlim(0);
        var release = new TaskCompletionSource();

        var first = ClaimAsync(id, loadedAttemptCount: 0, poised, release.Task);
        var second = ClaimAsync(id, loadedAttemptCount: 0, poised, release.Task);

        await poised.WaitAsync(Ct);
        await poised.WaitAsync(Ct);
        release.SetResult();

        var results = await Task.WhenAll(first, second);

        results.Count(x => x).ShouldBe(1);
        results.Count(x => !x).ShouldBe(1);

        var final = await _fixture.CreateContext().Set<WebhookDelivery>()
            .Where(x => x.Id == id)
            .Select(x => new { x.AttemptCount, x.NextAttemptAt })
            .FirstAsync(Ct);

        // The winner incremented exactly once; the loser matched zero rows and never touched the count. The
        // claim stamps AttemptCount and NextAttemptAt together, so the winner's next-attempt time is set too.
        final.AttemptCount.ShouldBe(1);
        final.NextAttemptAt.ShouldBe(NextAttempt);
    }

    [TimedFact]
    public async Task TryClaimAttempt_StaleAttemptCount_MatchesZeroRows()
    {
        // A claim whose loaded AttemptCount no longer matches the row (another executor already advanced it)
        // matches zero rows and loses — so it never proceeds to a second POST.
        var id = await SeedAsync(WebhookDeliveryStatus.Pending, attemptCount: 2);

        var claimed = await ExecuteWebhookDeliveryHandler<TestContext>.TryClaimAttemptAsync(
            _fixture.CreateContext(), id, loadedAttemptCount: 0, nextAttemptAt: null, Ct);

        claimed.ShouldBeFalse();
    }

    [TimedFact]
    public async Task TryClaimAttempt_SettledDelivery_MatchesZeroRows()
    {
        // A settled (non-Pending) delivery can never be claimed for another attempt.
        var id = await SeedAsync(WebhookDeliveryStatus.Delivered, attemptCount: 1);

        var claimed = await ExecuteWebhookDeliveryHandler<TestContext>.TryClaimAttemptAsync(
            _fixture.CreateContext(), id, loadedAttemptCount: 1, nextAttemptAt: null, Ct);

        claimed.ShouldBeFalse();
    }

    // A fixed, ms-truncated next-attempt time so the round-tripped value compares exactly on both providers.
    private static readonly DateTime NextAttempt = new(2026, 7, 10, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private async Task<bool> ClaimAsync(Guid id, int loadedAttemptCount, SemaphoreSlim poised, Task release)
    {
        var ctx = _fixture.CreateContext();
        poised.Release();
        await release;

        return await ExecuteWebhookDeliveryHandler<TestContext>.TryClaimAttemptAsync(ctx, id, loadedAttemptCount, NextAttempt, Ct);
    }

    private async Task<Guid> SeedAsync(WebhookDeliveryStatus status, int attemptCount)
    {
        var id = Guid.NewGuid();

        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = id,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            AttemptCount = attemptCount,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        return id;
    }
}

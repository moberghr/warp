using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Adapters;

/// <summary>
/// DB coverage for the cluster-shared adapter rate limiter (SC5, SC13). Two independent
/// <see cref="AdapterRateLimiter{TContext}"/> instances model two processes sharing one Warp database; a
/// <see cref="BarrierSignal"/> pins both on their first lease acquisition (N=2, §4.7) to force the
/// contended row-locked check-and-increment. No spray-N. Bare <c>[TimedFact]</c> throughout.
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterRateLimitTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected AdapterRateLimitTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task SharedLimit_TwoProcesses_AdmitsExactlyLimit_NoOverAdmission()
    {
        const string adapter = "concurrent";
        const int limit = 2;
        const int perSeconds = 60;

        var barrier = new BarrierSignal();
        var processOne = CreateLimiter();
        var processTwo = CreateLimiter();

        var taskOne = AttemptAsync(processOne, adapter, limit, perSeconds, attempts: 2, barrier);
        var taskTwo = AttemptAsync(processTwo, adapter, limit, perSeconds, attempts: 2, barrier);

        // Release both once each has pinned on its first lease acquisition (N=2).
        await barrier.Running.WaitAsync(Ct);
        await barrier.Running.WaitAsync(Ct);
        barrier.CanFinish.Release(2);

        var results = await Task.WhenAll(taskOne, taskTwo);

        var admitted = results.Sum(x => x.Admitted);
        var throttled = results.Sum(x => x.Throttled);
        admitted.ShouldBe(limit);
        throttled.ShouldBe(4 - limit);

        var bucket = await SingleContext().Set<RateLimitBucket>()
            .Where(x => x.Name == AdapterSharedPolicy.BucketKey(adapter))
            .FirstOrDefaultAsync(Ct);

        bucket.ShouldNotBeNull();
        bucket.CurrentCount.ShouldBe(limit);
    }

    [TimedFact]
    public async Task WaitOverflow_DelaysUntilWindowResets_ThenAdmits()
    {
        const string adapter = "wait-adapter";
        const int limit = 1;
        const int perSeconds = 1;

        var limiter = CreateLimiter();

        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.Wait, TimeSpan.FromSeconds(5), Ct);

        // The window budget is now exhausted; Wait must delay for the next window and then admit (no throw).
        // The two acquisitions are microseconds apart, so the second is denied inside the SAME 1s window and
        // must block until that window resets — a real, measurable delay, not an instant admission. (The
        // 1ms lower bound is far below the sub-second window-reset wait; a Wait that admitted instantly —
        // e.g. the overflow branch degenerating to FailFast semantics — would fail it.)
        var stopwatch = Stopwatch.StartNew();
        await Should.NotThrowAsync(async () =>
            await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.Wait, TimeSpan.FromSeconds(5), Ct));
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1));
    }

    [TimedFact]
    public async Task WaitOverflow_MaxWaitExpires_ThrowsWithoutUnboundedWait()
    {
        const string adapter = "wait-expiry-adapter";
        const int limit = 1;
        const int perSeconds = 60;

        var limiter = CreateLimiter();

        // Exhaust the single token in a 60s window that cannot reset within the wait budget.
        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.Wait, TimeSpan.FromMilliseconds(200), Ct);

        // Wait must bound the delay: it waits up to maxWait (200ms) for a window that resets in ~60s, then
        // throws instead of looping unboundedly.
        await Should.ThrowAsync<AdapterRateLimitedException>(async () =>
            await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.Wait, TimeSpan.FromMilliseconds(200), Ct));
    }

    [TimedFact]
    public async Task FailFastOverflow_ThrowsImmediately_WhenBudgetExhausted()
    {
        const string adapter = "failfast-adapter";
        const int limit = 1;
        const int perSeconds = 60;

        var limiter = CreateLimiter();

        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);

        await Should.ThrowAsync<AdapterRateLimitedException>(async () =>
            await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct));
    }

    [TimedFact]
    public async Task SharedPolicyConflict_EnforcesPersisted_FlagsDefinition_AndCountsConflict()
    {
        const string adapter = "conflict-adapter";
        const int persistedLimit = 3;
        const int persistedPerSeconds = 60;

        // A previous deploy persisted a different shared policy (limit 3); this process registers limit 1.
        var seed = SingleContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = adapter,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            SharedPolicyJson = AdapterSharedPolicy.ToJson(persistedLimit, persistedPerSeconds),
            SharedPolicyHash = AdapterSharedPolicy.Hash(persistedLimit, persistedPerSeconds),
        });
        await seed.SaveChangesAsync(Ct);

        var conflicts = 0L;
        using var listener = AdapterTestHarness.StartCounterListener("warp.adapter.config_conflicts", adapter, value => conflicts += value);

        var limiter = CreateLimiter();

        // Local limit is 1, but the persisted limit (3) is enforced: all three attempts admit.
        for (var i = 0; i < persistedLimit; i++)
        {
            await limiter.AcquireAsync(adapter, 1, persistedPerSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);
        }

        // A fourth attempt is over the persisted budget and fails fast.
        await Should.ThrowAsync<AdapterRateLimitedException>(async () =>
            await limiter.AcquireAsync(adapter, 1, persistedPerSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct));

        var definition = await SingleContext().Set<AdapterDefinition>()
            .Where(x => x.Name == adapter)
            .FirstAsync(Ct);

        definition.HasPolicyConflict.ShouldBeTrue();

        // Exactly one conflict increment per conflicting acquisition: every one of the persistedLimit admits
        // plus the one over-budget fail-fast attempt re-reads the persisted policy and re-detects the
        // mismatch (each acquisition drains its single-token lease, so each hits the DB slow path once).
        conflicts.ShouldBe(persistedLimit + 1);
    }

    [TimedFact]
    public async Task SharedPolicyConflict_LaterMatchingAcquisition_ClearsFlag()
    {
        const string adapter = "reconcile-adapter";
        const int limit = 2;
        const int perSeconds = 60;

        // A prior mismatching deploy left the conflict flag raised on the persisted policy. This process's
        // local policy now MATCHES the persisted one, so the flag must clear (entity-doc promise, F4).
        var seed = SingleContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = adapter,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            SharedPolicyJson = AdapterSharedPolicy.ToJson(limit, perSeconds),
            SharedPolicyHash = AdapterSharedPolicy.Hash(limit, perSeconds),
            HasPolicyConflict = true,
        });
        await seed.SaveChangesAsync(Ct);

        var limiter = CreateLimiter();

        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);

        var definition = await SingleContext().Set<AdapterDefinition>()
            .Where(x => x.Name == adapter)
            .FirstAsync(Ct);

        definition.HasPolicyConflict.ShouldBeFalse();
    }

    [TimedFact]
    public async Task LeasedTokens_ServedLocally_WithoutDbRoundTrip()
    {
        // The leasing design's whole point: with limit 50 the lease size is max(1, 50/10) = 5, so five
        // acquisitions must cost exactly ONE DB check-and-increment (bucket count 5), the other four
        // served from the banked local lease. A sixth acquisition opens the second lease (count 10).
        const string adapter = "leasing-adapter";
        const int limit = 50;
        const int perSeconds = 60;

        var limiter = CreateLimiter();

        for (var i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);
        }

        var bucket = await BucketAsync(adapter);
        bucket.CurrentCount.ShouldBe(5);

        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);

        (await BucketAsync(adapter)).CurrentCount.ShouldBe(10);
    }

    [TimedFact]
    public async Task StaleLocalLease_WindowElapsed_DiscardedAndRefreshedFromDb()
    {
        // Banked tokens are only valid inside their leased window. Once the window elapses, a remaining
        // local token must be discarded and a fresh DB lease taken in the NEW window — serving a stale
        // token would admit calls against a budget that already reset.
        const string adapter = "stale-lease-adapter";
        const int limit = 50;
        const int perSeconds = 60;

        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new AdapterRateLimiter<TestContext>(
            new FixtureScopeFactory(_fixture),
            time,
            NullLogger<AdapterRateLimiter<TestContext>>.Instance);

        // First acquisition banks a 5-token lease for the current window.
        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);
        var firstWindowStart = (await BucketAsync(adapter)).WindowStartUtc;

        time.Advance(TimeSpan.FromSeconds(perSeconds + 1));

        // Despite four locally banked tokens, the elapsed window forces a fresh DB lease.
        await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);

        var bucket = await BucketAsync(adapter);
        bucket.WindowStartUtc.ShouldBeGreaterThan(firstWindowStart);
        bucket.CurrentCount.ShouldBe(5);
    }

    [TimedFact]
    public async Task AdminOverride_WinsOverPersistedAndLocalPolicy()
    {
        // Runtime precedence (§8.19): RateLimitOverride admin row > persisted definition > local code. The
        // admin kill switch must actually bite — a key-format mismatch or precedence regression would make
        // it silently inert.
        const string adapter = "override-adapter";

        var seed = SingleContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = adapter,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
            SharedPolicyJson = AdapterSharedPolicy.ToJson(5, 60),
            SharedPolicyHash = AdapterSharedPolicy.Hash(5, 60),
        });
        seed.Set<RateLimitOverride>().Add(new RateLimitOverride
        {
            Name = AdapterSharedPolicy.BucketKey(adapter),
            Count = 1,
            WindowSeconds = 60,
            UpdatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync(Ct);

        var limiter = CreateLimiter();

        // Local policy says 10, persisted says 5 — the override's single token is what's enforced.
        await limiter.AcquireAsync(adapter, 10, 60, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);

        await Should.ThrowAsync<AdapterRateLimitedException>(async () =>
            await limiter.AcquireAsync(adapter, 10, 60, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct));
    }

    private async Task<RateLimitBucket> BucketAsync(string adapter)
    {
        return await SingleContext().Set<RateLimitBucket>()
            .AsNoTracking()
            .Where(x => x.Name == AdapterSharedPolicy.BucketKey(adapter))
            .FirstAsync(Ct);
    }

    private static async Task<(int Admitted, int Throttled)> AttemptAsync(
        AdapterRateLimiter<TestContext> limiter,
        string adapter,
        int limit,
        int perSeconds,
        int attempts,
        BarrierSignal barrier)
    {
        var pinned = false;
        limiter.BeforeLeaseAcquire = async ct =>
        {
            if (pinned)
            {
                return;
            }

            pinned = true;
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(ct);
        };

        var admitted = 0;
        var throttled = 0;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                await limiter.AcquireAsync(adapter, limit, perSeconds, AdapterRateLimitOverflow.FailFast, TimeSpan.Zero, Ct);
                admitted++;
            }
            catch (AdapterRateLimitedException)
            {
                throttled++;
            }
        }

        return (admitted, throttled);
    }

    private AdapterRateLimiter<TestContext> CreateLimiter()
        => new(
            new FixtureScopeFactory(_fixture),
            TimeProvider.System,
            NullLogger<AdapterRateLimiter<TestContext>>.Instance);

    private TestContext SingleContext() => _fixture.CreateContext();

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> that yields a fresh fixture-backed
    /// <see cref="TestContext"/> plus its provider-matched <see cref="IWarpSqlQueries{TContext}"/> and
    /// <see cref="IDatabaseExceptionClassifier"/>, so each limiter instance models an independent process
    /// against the shared fixture database without coupling the test to provider-specific DI wiring.
    /// </summary>
    private sealed class FixtureScopeFactory : IServiceScopeFactory
    {
        private readonly IDatabaseFixture _fixture;

        public FixtureScopeFactory(IDatabaseFixture fixture) => _fixture = fixture;

        public IServiceScope CreateScope() => new FixtureScope(_fixture);

        private sealed class FixtureScope : IServiceScope, IServiceProvider
        {
            private readonly TestContext _context;
            private readonly IWarpSqlQueries<TestContext> _queries;
            private readonly IDatabaseExceptionClassifier _classifier;

            public FixtureScope(IDatabaseFixture fixture)
            {
                _context = fixture.CreateContext();
                _queries = TestTasks.QueriesFor(_context);
                _classifier = TestTasks.ClassifierFor(_context);
            }

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(TestContext))
                {
                    return _context;
                }

                if (serviceType == typeof(IWarpSqlQueries<TestContext>))
                {
                    return _queries;
                }

                if (serviceType == typeof(IDatabaseExceptionClassifier))
                {
                    return _classifier;
                }

                return null;
            }

            public void Dispose() => _context.Dispose();
        }
    }
}

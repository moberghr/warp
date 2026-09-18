using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Observability;

/// <summary>
/// A job whose success-path write failed must be counted once, as failed — never as both.
/// <para>
/// The worker stages a finalization's counter increments on itself and hands them to the shared buffer
/// only after the transaction commits. When that commit throws, control lands in the failure arm, which
/// finalizes the SAME job a second time and commits successfully — so anything still staged from the
/// abandoned attempt rides across with the failure's own increments and <c>stats:succeeded</c> and
/// <c>stats:failed</c> both move for one job. That breaks the reconciliation §8.33 requires and lets the
/// attributed reason breakdown exceed its own state total, silently: the job row is correct, the metrics
/// are not, and nothing in the run looks wrong.
/// </para>
/// <para>
/// Reaching it needs fault injection, which is why the sibling
/// <see cref="StagedCounterCommitTestsBase"/> could not cover it: both arms write the same rows through
/// the same context, so there is no asymmetry in the data to exploit. The fault goes in through
/// <see cref="IWarpServerContextConfigurator"/> — the seam the providers use to configure the server
/// context — so the worker hot path keeps no test hook (§0.2/§6.1).
/// </para>
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class FailedFinalizationCounterTestsBase : IntegrationTestBase
{
    protected FailedFinalizationCounterTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact(30_000)]
    public async Task StagedCounters_OfAnAttemptWhoseFinalizationFailed_DoNotRideTheFailureArmsCommit()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            config =>
            {
                config.WorkerCount = 1;

                // The background flusher would otherwise drain the buffer before the assertion reads it.
                config.CounterBufferFlushInterval = TimeSpan.FromMinutes(10);
            },
            FailTheFirstCompletion);

        var publisher = server.CreatePublisher();
        var jobId = await publisher.Enqueue(new UnitRequest());
        await publisher.SaveChangesAsync();

        // The handler succeeds; the write that records it does not, so the worker re-finalizes as Failed.
        await server.WaitForJobState(jobId, State.Failed);

        var keys = server.CounterBuffer.Drain().Select(x => x.Key).ToList();

        keys.ShouldContain(
            "stats:failed",
            "the arm that actually committed must still hand its own increments to the buffer");

        keys.ShouldNotContain(
            "stats:succeeded",
            "the attempt that rolled back must contribute nothing — one job cannot be both");
    }

    /// <summary>
    /// Replaces the provider's server-context configurator with one that delegates to it and adds the
    /// interceptor. Registered last so <c>GetService</c> returns it; the provider's own instance is taken
    /// from the descriptor so none of its connection wiring is reimplemented here.
    /// </summary>
    private static void FailTheFirstCompletion(IServiceCollection services)
    {
        var inner = (IWarpServerContextConfigurator)services
            .Last(x => x.ServiceType == typeof(IWarpServerContextConfigurator))
            .ImplementationInstance!;

        services.AddSingleton<IWarpServerContextConfigurator>(
            new InterceptingConfigurator(inner, new FailFirstCompletionInterceptor()));
    }

    private sealed class InterceptingConfigurator : IWarpServerContextConfigurator
    {
        private readonly IWarpServerContextConfigurator _inner;
        private readonly ISaveChangesInterceptor _interceptor;

        public InterceptingConfigurator(IWarpServerContextConfigurator inner, ISaveChangesInterceptor interceptor)
        {
            _inner = inner;
            _interceptor = interceptor;
        }

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            _inner.Configure(optionsBuilder, applicationServices);
            optionsBuilder.AddInterceptors(_interceptor);
        }
    }

    /// <summary>
    /// Throws once, on the write that records a job as Completed. Everything after it — including the
    /// failure arm's own write of the same job — goes through, which is the whole point: the failure arm
    /// has to reach its commit for the abandoned attempt's increments to be observable.
    /// </summary>
    private sealed class FailFirstCompletionInterceptor : SaveChangesInterceptor
    {
        private int _fired;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var completing = eventData.Context?.ChangeTracker
                .Entries<Job>()
                .Any(x => x.State == EntityState.Modified && x.Entity.CurrentState == State.Completed) == true;

            if (completing && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                throw new InvalidOperationException("Injected finalization failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}

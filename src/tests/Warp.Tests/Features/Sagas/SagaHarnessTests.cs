using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Sagas;
using Warp.Core.Sagas.Testing;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Sagas;

namespace Warp.Tests.Features.Sagas;

/// <summary>
/// Worker-free saga tests driven entirely through <see cref="SagaTestHarness{TContext}"/> against a
/// SQLite in-memory database and the in-process lock provider. Exercises the full dispatch — create,
/// converge, complete, dead-letter, timeout — without booting a <c>WarpTestServer</c> or the
/// <c>MessageRouter</c>. Fast tier (<c>NoDb</c>): no container, no fixture.
/// </summary>
[Trait("Category", "NoDb")]
public class SagaHarnessTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private SagaTestHarness<HarnessContext> _harness = null!;

    private static CancellationToken CT => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(CT);
        _harness = SagaTestHarness<HarnessContext>.Create(
            o => o.UseSqlite(_connection),
            s => s.AddSagaHandler<OrderSagaHandler>());
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [TimedFact]
    public async Task DispatchAsync_StartsSagaMessage_CreatesSaga()
    {
        var result = await _harness.DispatchAsync(new OrderPlaced { OrderId = "O-1" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.Created);
        result.JobOutcome.ShouldBeNull();

        var saga = await _harness.GetSagaAsync<OrderSaga>("O-1", CT);
        saga.ShouldNotBeNull();
        saga.OrderId.ShouldBe("O-1");
        (await _harness.CountAsync<OrderSaga>(CT)).ShouldBe(1);
    }

    [TimedFact]
    public async Task DispatchAsync_SecondCorrelatedMessage_UpdatesSaga()
    {
        await _harness.DispatchAsync(new OrderPlaced { OrderId = "O-2" }, CT);

        var result = await _harness.DispatchAsync(new PaymentCaptured { OrderId = "O-2" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.Updated);
        result.JobOutcome.ShouldBeNull();

        var saga = await _harness.GetSagaAsync<OrderSaga>("O-2", CT);
        saga.ShouldNotBeNull();
        saga.PaymentCaptured.ShouldBeTrue();
        saga.InventoryReserved.ShouldBeFalse();
    }

    [TimedFact]
    public async Task DispatchAsync_FinalConvergingMessage_CompletesAndRemovesSaga()
    {
        await _harness.DispatchAsync(new OrderPlaced { OrderId = "O-3" }, CT);
        await _harness.DispatchAsync(new PaymentCaptured { OrderId = "O-3" }, CT);

        var result = await _harness.DispatchAsync(new InventoryReserved { OrderId = "O-3" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.Completed);
        result.JobOutcome.ShouldBeNull();

        (await _harness.GetSagaAsync<OrderSaga>("O-3", CT)).ShouldBeNull();
        (await _harness.CountAsync<OrderSaga>(CT)).ShouldBe(0);
    }

    [TimedFact]
    public async Task DispatchAsync_UnknownCorrelation_NonStartMessage_DeadLetters()
    {
        var result = await _harness.DispatchAsync(new PaymentCaptured { OrderId = "never-existed" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.NotFound);
        result.JobOutcome.ShouldNotBeNull();
        result.JobOutcome.State.ShouldBe(State.Failed);
        result.JobOutcome.LogMessage!.ShouldContain("No saga");

        (await _harness.GetSagaAsync<OrderSaga>("never-existed", CT)).ShouldBeNull();
    }

    [TimedFact]
    public async Task DispatchAsync_TimeoutForLiveSaga_FiresHandlerAndCompletes()
    {
        var created = await _harness.DispatchAsync(new OrderPlaced { OrderId = "O-4" }, CT);
        created.Outcome.ShouldBe(SagaDispatchOutcome.Created);

        // OrderSagaHandler's OrderTimeout branch sets TimedOut and calls MarkCompleted, so a fired
        // timeout on a live saga completes it — the row removal is the observable proof it ran.
        var result = await _harness.DispatchAsync(new OrderTimeout { OrderId = "O-4" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.Completed);
        (await _harness.GetSagaAsync<OrderSaga>("O-4", CT)).ShouldBeNull();
    }

    [TimedFact]
    public async Task DispatchAsync_TimeoutForMissingSaga_IsDroppedNotFailed()
    {
        var result = await _harness.DispatchAsync(new OrderTimeout { OrderId = "already-gone" }, CT);

        result.Outcome.ShouldBe(SagaDispatchOutcome.TimeoutDropped);
        result.JobOutcome.ShouldNotBeNull();
        result.JobOutcome.State.ShouldBe(State.Deleted);
        result.JobOutcome.LogMessage!.ShouldContain("moot");
    }

    [TimedFact]
    public async Task DispatchAsync_LockHeld_ReportsBusy_WithoutInvokingHandler()
    {
        // Bring-your-own-provider path so the test can hold the correlation-key lock the proxy checks.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(CT);
        try
        {
            var services = new ServiceCollection();
            services.AddDbContext<HarnessContext>(o => o.UseSqlite(connection));
            services.AddWarp<HarnessContext>(opt =>
            {
                opt.Schema = null;
                opt.UseInProcessLock();
                opt.AddSagas();
            });
            services.AddSagaHandler<OrderSagaHandler>();
            services.AddSingleton<Warp.Core.Data.IDatabaseExceptionClassifier, FakeExceptionClassifier>();

            await using var provider = services.BuildServiceProvider(validateScopes: true);
            await using (var scope = provider.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HarnessContext>().Database.EnsureCreatedAsync(CT);
            }

            var harness = new SagaTestHarness<HarnessContext>(provider);
            var locks = provider.GetRequiredService<IWarpLockProvider>();

            await using var held = await locks.TryAcquireAsync(
                $"warp:saga:{typeof(OrderSaga).FullName}:O-busy", TimeSpan.Zero, CT);
            held.ShouldNotBeNull();

            var result = await harness.DispatchAsync(new OrderPlaced { OrderId = "O-busy" }, CT);

            result.Outcome.ShouldBe(SagaDispatchOutcome.Busy);
            result.JobOutcome.ShouldNotBeNull();
            result.JobOutcome.State.ShouldBe(State.Scheduled);
            result.JobOutcome.LogMessage!.ShouldContain("busy");

            // Handler never ran, so nothing was persisted.
            (await harness.CountAsync<OrderSaga>(CT)).ShouldBe(0);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    public sealed class HarnessContext(DbContextOptions<HarnessContext> options) : DbContext(options);
}

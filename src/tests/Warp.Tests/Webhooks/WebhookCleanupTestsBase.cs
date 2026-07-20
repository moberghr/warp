using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Webhooks;

/// <summary>
/// <c>ExpirationCleanup</c> coverage for the <see cref="WebhookDelivery"/> table (WSC9): delivery rows are
/// deleted once past their stamped <c>ExpireAt</c>; unexpired rows and rows with a null <c>ExpireAt</c> are
/// kept. The table is always in the schema (§2.11), so the sweep runs unconditionally like the adapter
/// call-log sweep. Each test drives exactly one cleanup method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WebhookCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Cleanup_ExpiredDelivery_Deleted()
    {
        var id = await InsertDeliveryAsync(DateTime.UtcNow.AddHours(-1));

        await CreateCleanup().CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        (await DeliveryExistsAsync(id)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Cleanup_UnexpiredDelivery_Kept()
    {
        var id = await InsertDeliveryAsync(DateTime.UtcNow.AddHours(1));

        await CreateCleanup().CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        (await DeliveryExistsAsync(id)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_DeliveryWithNullExpireAt_Kept()
    {
        var id = await InsertDeliveryAsync(expireAt: null);

        await CreateCleanup().CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        (await DeliveryExistsAsync(id)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_ExpiredPendingDelivery_Kept()
    {
        // W-6: an in-flight (Pending) delivery whose ExpireAt elapsed mid-schedule must never be swept out
        // from under its own scheduled executor job. Only settled rows are eligible.
        var id = await InsertDeliveryAsync(DateTime.UtcNow.AddHours(-1), WebhookDeliveryStatus.Pending);

        await CreateCleanup().CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        (await DeliveryExistsAsync(id)).ShouldBeTrue();
    }

    [TimedFact]
    public async Task Cleanup_ExpiredExhaustedDelivery_Deleted()
    {
        var id = await InsertDeliveryAsync(DateTime.UtcNow.AddHours(-1), WebhookDeliveryStatus.Exhausted);

        await CreateCleanup().CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        (await DeliveryExistsAsync(id)).ShouldBeFalse();
    }

    [TimedFact]
    public async Task Cleanup_ExpiredBacklogBeyondBatchSize_FullyDrainedInBoundedBatches()
    {
        // Volume guard: the sweep deletes in ExpirationBatchSize id batches (bounded statements), but a
        // backlog larger than one batch must still fully drain within a single tick.
        for (var i = 0; i < 5; i++)
        {
            await InsertDeliveryAsync(DateTime.UtcNow.AddHours(-1));
        }

        var kept = await InsertDeliveryAsync(DateTime.UtcNow.AddHours(1));

        var deleted = await CreateCleanup(batchSize: 2).CleanupExpiredWebhookDeliveriesAsync(Xunit.TestContext.Current.CancellationToken);

        deleted.ShouldBe(5);
        (await DeliveryExistsAsync(kept)).ShouldBeTrue();
        (await _fixture.CreateContext().Set<WebhookDelivery>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    private ExpirationCleanup<TestContext> CreateCleanup(int? batchSize = null)
        => new(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(new WarpServerConfiguration
            {
                ExpirationBatchSize = batchSize ?? new WarpServerConfiguration().ExpirationBatchSize,
            }));

    private async Task<Guid> InsertDeliveryAsync(DateTime? expireAt, WebhookDeliveryStatus status = WebhookDeliveryStatus.Delivered)
    {
        var ctx = _fixture.CreateContext();
        var delivery = new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            PayloadJson = "{\"order\":42}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = status,
            CreatedAt = DateTime.UtcNow,
            ExpireAt = expireAt,
        };

        ctx.Set<WebhookDelivery>().Add(delivery);
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return delivery.Id;
    }

    private async Task<bool> DeliveryExistsAsync(Guid id)
    {
        return await _fixture.CreateContext().Set<WebhookDelivery>()
            .AnyAsync(x => x.Id == id, Xunit.TestContext.Current.CancellationToken);
    }
}

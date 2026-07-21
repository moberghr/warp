using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

// PG-only regression guard: the delivery-statistics hour bucketing must be UTC-correct. created_at is a
// timestamptz, and a naive bare date_part() would be evaluated in the Postgres session TimeZone (shifting
// buckets on a non-UTC session, diverging from SQL Server). Npgsql's EF provider translates the
// DateTime-part GroupBy to date_part(..., created_at AT TIME ZONE 'UTC'), so it IS session-independent —
// this test pins that so a future query rewrite can't silently reintroduce a session-dependent extraction.
[Trait("Category", "PostgreSql")]
public class WebhookDeliveryHistoryTimeZoneTests : IAsyncLifetime, IClassFixture<PostgreSqlClassFixture>
{
    private readonly PostgreSqlClassFixture _fixture;

    public WebhookDeliveryHistoryTimeZoneTests(PostgreSqlClassFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetDeliveryHistory_BucketsByUtcHour_IndependentOfSessionTimeZone()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var hourA = new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc);
        var hourB = new DateTime(2026, 7, 16, 10, 0, 0, DateTimeKind.Utc);

        var seed = _fixture.CreateContext();
        seed.Set<WebhookDelivery>().Add(Delivery(hourA.AddMinutes(5)));
        seed.Set<WebhookDelivery>().Add(Delivery(hourA.AddMinutes(40)));
        seed.Set<WebhookDelivery>().Add(Delivery(hourB.AddMinutes(20)));
        await seed.SaveChangesAsync(ct);

        // Guard: the DateTime-part grouping Npgsql emits for a timestamptz normalizes to UTC, so the buckets
        // are NOT evaluated in the session TimeZone. Replicates GetDeliveryHistory's grouping shape.
        var sql = _fixture.CreateContext().Set<WebhookDelivery>()
            .GroupBy(x => new { x.CreatedAt.Year, x.CreatedAt.Month, x.CreatedAt.Day, x.CreatedAt.Hour })
            .Select(g => new { g.Key.Hour, Count = g.Count() })
            .ToQueryString();
        sql.ShouldContain("AT TIME ZONE 'UTC'");

        var history = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveryHistory(new WebhookDeliveryFilter(), ct);

        history.Count.ShouldBe(2);
        history[0].Hour.ShouldBe(hourA);
        history[0].Total.ShouldBe(2);
        history[1].Hour.ShouldBe(hourB);
        history[1].Total.ShouldBe(1);
    }

    private static WebhookDelivery Delivery(DateTime createdAt) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "order.shipped",
        EventId = Guid.NewGuid().ToString(),
        Url = "https://x.test/hook",
        PayloadJson = "{}",
        SigningMode = WebhookSigning.None,
        RetrySchedule = [],
        Status = WebhookDeliveryStatus.Delivered,
        CreatedAt = createdAt,
    };
}

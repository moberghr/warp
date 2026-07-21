using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// Persistence coverage for <see cref="WebhookDelivery"/> (WSC1): a fully-populated self-contained row
/// survives a write + read on both providers, and the <see cref="WebhookQueryService{TContext}"/>
/// projections always redact the per-delivery secret and <c>Authorization</c>-class headers (§1.2).
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookDeliveryPersistenceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected WebhookDeliveryPersistenceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Persist_FullyPopulatedRow_AllFieldsSurvive()
    {
        var id = Guid.NewGuid();
        var createdAt = new DateTime(2026, 7, 10, 8, 30, 0, DateTimeKind.Utc);
        var nextAttempt = createdAt.AddMinutes(10);
        var expireAt = createdAt.AddDays(30);

        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = id,
            EventType = "order.created",
            EventId = "evt-123",
            Url = "https://example.test/hook",
            HeadersJson = "{\"Content-Type\":\"application/json\"}",
            GroupName = "endpoint-eu",
            Reference = "sub-42",
            PayloadJson = "{\"order\":42}",
            SigningMode = WebhookSigning.StandardWebhooks,
            Secret = "whsec_abc",
            RetrySchedule = [TimeSpan.FromMinutes(1), TimeSpan.FromHours(6)],
            SuccessCodesJson = "[200,202]",
            Status = WebhookDeliveryStatus.Pending,
            AttemptCount = 1,
            NextAttemptAt = nextAttempt,
            CreatedAt = createdAt,
            ExpireAt = expireAt,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var row = await _fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        row.ShouldNotBeNull();
        row.EventType.ShouldBe("order.created");
        row.EventId.ShouldBe("evt-123");
        row.Url.ShouldBe("https://example.test/hook");
        row.HeadersJson.ShouldBe("{\"Content-Type\":\"application/json\"}");
        row.GroupName.ShouldBe("endpoint-eu");
        row.Reference.ShouldBe("sub-42");
        row.PayloadJson.ShouldBe("{\"order\":42}");
        row.SigningMode.ShouldBe(WebhookSigning.StandardWebhooks);
        row.Secret.ShouldBe("whsec_abc");
        row.RetrySchedule.ShouldBe([TimeSpan.FromMinutes(1), TimeSpan.FromHours(6)]);
        row.SuccessCodesJson.ShouldBe("[200,202]");
        row.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        row.AttemptCount.ShouldBe(1);
        row.NextAttemptAt.ShouldBe(nextAttempt);
        row.CreatedAt.ShouldBe(createdAt);
        row.ExpireAt.ShouldBe(expireAt);
    }

    [TimedFact]
    public async Task Persist_NullableFields_RoundtripAsNull()
    {
        var id = await InsertAsync(secret: null, headersJson: null, reference: null, groupName: null);

        var row = await _fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        row.ShouldNotBeNull();
        row.Secret.ShouldBeNull();
        row.HeadersJson.ShouldBeNull();
        row.Reference.ShouldBeNull();
        row.GroupName.ShouldBeNull();
        row.NextAttemptAt.ShouldBeNull();
        row.ExpireAt.ShouldBeNull();
    }

    [TimedFact]
    public async Task GetDeliveryDetail_WithSecret_NeverExposesSecretValue()
    {
        var id = await InsertAsync(secret: "whsec_supersecret", headersJson: null, reference: null, groupName: null);

        var detail = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveryDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.HasSecret.ShouldBeTrue();
    }

    [TimedFact]
    public async Task GetDeliveryDetail_RedactsAuthorizationClassHeaders()
    {
        var headers = "{\"Authorization\":\"Bearer super-secret\",\"X-Api-Key\":\"key-123\",\"Content-Type\":\"application/json\"}";
        var id = await InsertAsync(secret: null, headersJson: headers, reference: null, groupName: null);

        var detail = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveryDetail(id, Xunit.TestContext.Current.CancellationToken);

        detail.ShouldNotBeNull();
        detail.HeadersJson.ShouldNotBeNull();
        detail.HeadersJson.ShouldNotContain("super-secret");
        detail.HeadersJson.ShouldNotContain("key-123");
        detail.HeadersJson.ShouldContain("application/json");
        detail.HeadersJson.ShouldContain("***");
    }

    [TimedFact]
    public async Task GetDeliveries_FilterByStatus_ReturnsOnlyMatching()
    {
        await InsertAsync(secret: null, headersJson: null, reference: null, groupName: null, status: WebhookDeliveryStatus.Pending);
        await InsertAsync(secret: null, headersJson: null, reference: null, groupName: null, status: WebhookDeliveryStatus.Delivered);

        var results = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { Status = WebhookDeliveryStatus.Delivered }, Xunit.TestContext.Current.CancellationToken);

        results.Items.ShouldHaveSingleItem().Status.ShouldBe(WebhookDeliveryStatus.Delivered);
    }

    [TimedFact]
    public async Task GetDeliveries_FilterByEventType_ReturnsOnlyMatching()
    {
        await InsertFilterRowAsync(eventType: "order.created");
        await InsertFilterRowAsync(eventType: "order.shipped");

        var results = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { EventType = "order.created" }, Xunit.TestContext.Current.CancellationToken);

        results.Items.ShouldHaveSingleItem().EventType.ShouldBe("order.created");
    }

    [TimedFact]
    public async Task GetDeliveries_FilterByReference_ReturnsOnlyMatching()
    {
        await InsertFilterRowAsync(reference: "sub-1");
        await InsertFilterRowAsync(reference: "sub-2");

        var results = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { Reference = "sub-1" }, Xunit.TestContext.Current.CancellationToken);

        results.Items.ShouldHaveSingleItem().Reference.ShouldBe("sub-1");
    }

    [TimedFact]
    public async Task GetDeliveries_FilterByGroupName_ReturnsOnlyMatching()
    {
        await InsertFilterRowAsync(groupName: "endpoint-eu");
        await InsertFilterRowAsync(groupName: "endpoint-us");

        var results = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { GroupName = "endpoint-eu" }, Xunit.TestContext.Current.CancellationToken);

        results.Items.ShouldHaveSingleItem().GroupName.ShouldBe("endpoint-eu");
    }

    [TimedFact]
    public async Task GetDeliveries_FilterBySinceUntilRange_ExcludesRowsOutsideRange()
    {
        var inside = new DateTime(2026, 7, 10, 12, 0, 0, DateTimeKind.Utc);
        await InsertFilterRowAsync(reference: "before", createdAt: inside.AddHours(-2));
        await InsertFilterRowAsync(reference: "inside", createdAt: inside);
        await InsertFilterRowAsync(reference: "after", createdAt: inside.AddHours(2));

        var results = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(
                new WebhookDeliveryFilter { Since = inside.AddHours(-1), Until = inside.AddHours(1) },
                Xunit.TestContext.Current.CancellationToken);

        results.Items.ShouldHaveSingleItem().Reference.ShouldBe("inside");
    }

    private async Task InsertFilterRowAsync(
        string eventType = "order.created",
        string? reference = null,
        string? groupName = null,
        DateTime? createdAt = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            GroupName = groupName,
            Reference = reference,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = WebhookDeliveryStatus.Pending,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    [TimedFact]
    public async Task GetDeliveries_IdenticalCreatedAt_OrdersByIdDescendingAsStableTiebreaker()
    {
        // SMALL-5: OrderByDescending(CreatedAt) alone leaves the page boundary non-deterministic when
        // CreatedAt ties. ThenByDescending(Id) gives a total order, so the Take(N) page is stable and matches
        // the fully ordered prefix. Five rows share one CreatedAt; the page must equal the deterministic first 3.
        var createdAt = new DateTime(2026, 7, 10, 8, 30, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            await InsertWithCreatedAtAsync(Guid.NewGuid(), createdAt);
        }

        var page = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { PageSize = 3 }, Xunit.TestContext.Current.CancellationToken);

        // The same total order the service applies (CreatedAt desc, then Id desc) — the deterministic prefix.
        var expected = await _fixture.CreateContext().Set<WebhookDelivery>()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .Take(3)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        page.Items.Select(x => x.Id).ShouldBe(expected);
    }

    [TimedFact]
    public async Task GetDeliveries_PageSizeAboveMaxPageSize_ClampedTo200()
    {
        // The one place caller-controlled numeric input reaches a Take(): a page size of 100000 from the
        // endpoint must clamp to MaxPageSize (200), not materialise the whole table.
        var createdAt = new DateTime(2026, 7, 10, 8, 30, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 205; i++)
        {
            ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
            {
                Id = Guid.NewGuid(),
                EventType = "order.created",
                EventId = Guid.NewGuid().ToString(),
                Url = "https://example.test/hook",
                PayloadJson = "{}",
                SigningMode = WebhookSigning.None,
                RetrySchedule = [],
                Status = WebhookDeliveryStatus.Pending,
                CreatedAt = createdAt,
            });
        }

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var page = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { PageSize = 100_000 }, Xunit.TestContext.Current.CancellationToken);

        page.Items.Count.ShouldBe(200);
    }

    [TimedFact]
    public async Task GetDeliveries_NonPositivePageSize_FallsBackToDefaultPageSize()
    {
        // Pins the documented semantics: PageSize <= 0 means "not specified" (default page of 20), NOT
        // "return nothing" — the dashboard omits the field for an unset page size.
        await InsertWithCreatedAtAsync(Guid.NewGuid(), DateTime.UtcNow);

        var page = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { PageSize = 0 }, Xunit.TestContext.Current.CancellationToken);

        page.Items.Count.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetDeliveries_Paging_ReturnsRequestedPageWithTotals()
    {
        // Five rows on distinct timestamps (newest first) → page 1 of size 2 is the 3rd+4th newest.
        var baseAt = new DateTime(2026, 7, 10, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            await InsertWithCreatedAtAsync(Guid.NewGuid(), baseAt.AddMinutes(i));
        }

        var page = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetDeliveries(new WebhookDeliveryFilter { Page = 1, PageSize = 2 }, Xunit.TestContext.Current.CancellationToken);

        page.TotalCount.ShouldBe(5);
        page.PageCount.ShouldBe(3);
        page.Items.Count.ShouldBe(2);

        var expected = await _fixture.CreateContext().Set<WebhookDelivery>()
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Id)
            .Skip(2)
            .Take(2)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        page.Items.Select(x => x.Id).ShouldBe(expected);
    }

    [TimedFact]
    public async Task GetGroups_ByEventType_CountsPerStatus()
    {
        await InsertGroupedAsync("order.created", groupName: "ep-eu", status: WebhookDeliveryStatus.Delivered);
        await InsertGroupedAsync("order.created", groupName: "ep-eu", status: WebhookDeliveryStatus.Pending);
        await InsertGroupedAsync("order.shipped", groupName: "ep-eu", status: WebhookDeliveryStatus.Exhausted);

        var groups = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetGroups(WebhookGroupBy.EventType, Xunit.TestContext.Current.CancellationToken);

        groups.Count.ShouldBe(2);

        var created = groups.Single(x => string.Equals(x.Key, "order.created", StringComparison.Ordinal));
        created.Total.ShouldBe(2);
        created.Delivered.ShouldBe(1);
        created.Pending.ShouldBe(1);
        created.Exhausted.ShouldBe(0);

        var shipped = groups.Single(x => string.Equals(x.Key, "order.shipped", StringComparison.Ordinal));
        shipped.Total.ShouldBe(1);
        shipped.Exhausted.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetGroups_ByEndpoint_GroupsByGroupNameFallingBackToUrl()
    {
        await InsertGroupedAsync("order.created", groupName: "ep-eu", status: WebhookDeliveryStatus.Delivered);
        await InsertGroupedAsync("order.created", groupName: "ep-eu", status: WebhookDeliveryStatus.Delivered);
        await InsertGroupedAsync("order.created", groupName: null, url: "https://raw.test/hook", status: WebhookDeliveryStatus.Pending);

        var groups = await new WebhookQueryService<TestContext>(_fixture.CreateContext())
            .GetGroups(WebhookGroupBy.Endpoint, Xunit.TestContext.Current.CancellationToken);

        groups.Count.ShouldBe(2);
        groups.Single(x => string.Equals(x.Key, "ep-eu", StringComparison.Ordinal)).Total.ShouldBe(2);

        // A delivery with no group falls back to its URL as the endpoint key.
        groups.Single(x => string.Equals(x.Key, "https://raw.test/hook", StringComparison.Ordinal)).Total.ShouldBe(1);
    }

    private async Task InsertGroupedAsync(
        string eventType,
        string? groupName,
        WebhookDeliveryStatus status,
        string url = "https://example.test/hook")
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            EventId = Guid.NewGuid().ToString(),
            Url = url,
            GroupName = groupName,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            CreatedAt = DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task InsertWithCreatedAtAsync(Guid id, DateTime createdAt)
    {
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
            Status = WebhookDeliveryStatus.Pending,
            CreatedAt = createdAt,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<Guid> InsertAsync(
        string? secret,
        string? headersJson,
        string? reference,
        string? groupName,
        WebhookDeliveryStatus status = WebhookDeliveryStatus.Pending)
    {
        var id = Guid.NewGuid();

        var ctx = _fixture.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = id,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = "https://example.test/hook",
            HeadersJson = headersJson,
            GroupName = groupName,
            Reference = reference,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            Secret = secret,
            RetrySchedule = [TimeSpan.FromMinutes(1)],
            Status = status,
            CreatedAt = DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        return id;
    }
}

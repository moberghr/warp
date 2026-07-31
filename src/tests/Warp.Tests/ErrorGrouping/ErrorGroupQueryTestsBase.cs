using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.ErrorGrouping;

/// <summary>
/// Read side of error grouping / Issues (§8.29): <see cref="ErrorGroupQueryService{TContext}"/> lists the durable
/// <see cref="ErrorGroup"/> rows with per-field filters + paging, and builds a group's detail — including the
/// hourly trend folded from the durable <c>errorgroup:</c> Statistic keys (survives raw-row cleanup, §8.22) and
/// the computed <c>IsNew</c> / <c>IsRegressed</c> flags. Both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class ErrorGroupQueryTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ErrorGroupQueryTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private ErrorGroupQueryService<TestContext> Service() => new(_fixture.CreateContext(), TimeProvider.System);

    [TimedFact]
    public async Task GetGroups_FiltersBySourceStatusAndApplication_WithTotal()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().AddRange(
            Group("fp-job-shop", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop"),
            Group("fp-job-admin", ErrorSource.Job, ErrorGroupStatus.Unresolved, "admin"),
            Group("fp-client-shop", ErrorSource.Client, ErrorGroupStatus.Unresolved, "shop"),
            Group("fp-job-shop-resolved", ErrorSource.Job, ErrorGroupStatus.Resolved, "shop"));
        await ctx.SaveChangesAsync(Ct);

        (await Service().GetGroups(ErrorSource.Job, null, null, null, 0, 50, Ct)).Total.ShouldBe(3);
        (await Service().GetGroups(null, ErrorGroupStatus.Unresolved, null, null, 0, 50, Ct)).Total.ShouldBe(3);
        (await Service().GetGroups(null, null, "shop", null, 0, 50, Ct)).Total.ShouldBe(3);
        (await Service().GetGroups(ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop", null, 0, 50, Ct)).Total.ShouldBe(1);
    }

    [TimedFact]
    public async Task GetGroups_FiltersByKind()
    {
        var ctx = _fixture.CreateContext();
        var exception = Group("fp-exc", ErrorSource.Endpoint, ErrorGroupStatus.Unresolved, "shop");
        exception.Kind = ErrorKind.Exception;
        var statusCode = Group("fp-422", ErrorSource.Endpoint, ErrorGroupStatus.Unresolved, "shop");
        statusCode.Kind = ErrorKind.StatusCode;
        statusCode.StatusCode = 422;
        ctx.Set<ErrorGroup>().AddRange(exception, statusCode);
        await ctx.SaveChangesAsync(Ct);

        var list = await Service().GetGroups(null, null, null, ErrorKind.StatusCode, 0, 50, Ct);

        list.Total.ShouldBe(1);
        list.Items.ShouldHaveSingleItem().Fingerprint.ShouldBe("fp-422");
    }

    [TimedFact]
    public async Task GetGroups_OrdersByLastSeenDescAndPages()
    {
        var basis = new DateTime(2026, 7, 28, 10, 0, 0, DateTimeKind.Utc);
        var ctx = _fixture.CreateContext();
        for (var i = 0; i < 3; i++)
        {
            var g = Group($"fp-{i}", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop");
            g.LastSeenAt = basis.AddMinutes(i);   // fp-2 is newest
            ctx.Set<ErrorGroup>().Add(g);
        }

        await ctx.SaveChangesAsync(Ct);

        var page = await Service().GetGroups(null, null, null, null, 0, 2, Ct);

        page.Total.ShouldBe(3);
        page.Items.Count.ShouldBe(2);
        page.Items[0].Fingerprint.ShouldBe("fp-2");   // newest first
        page.Items[1].Fingerprint.ShouldBe("fp-1");
    }

    [TimedFact]
    public async Task GetGroup_ReturnsDetailWithTrendFromStatistics()
    {
        const string fingerprint = "fp-trend";
        var ctx = _fixture.CreateContext();
        ctx.Set<ErrorGroup>().Add(Group(fingerprint, ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop"));

        // Durable hourly trend keys: errorgroup:{fp}:{yyyyMMddHH}. Three ascending buckets.
        var hour0 = new DateTime(2026, 7, 28, 8, 0, 0, DateTimeKind.Utc);
        ctx.Set<Statistic>().AddRange(
            Stat(fingerprint, hour0, 2),
            Stat(fingerprint, hour0.AddHours(1), 5),
            Stat(fingerprint, hour0.AddHours(2), 1));
        await ctx.SaveChangesAsync(Ct);

        var detail = await Service().GetGroup(fingerprint, Ct);

        detail.ShouldNotBeNull();
        detail!.Fingerprint.ShouldBe(fingerprint);
        detail.Trend.Count.ShouldBe(3);
        detail.Trend.Select(x => x.Count).ShouldBe([2, 5, 1]);   // ascending by hour
        detail.Trend[0].Hour.ShouldBe(hour0);
    }

    [TimedFact]
    public async Task GetGroup_UnknownFingerprint_ReturnsNull()
    {
        (await Service().GetGroup("nope", Ct)).ShouldBeNull();
    }

    [TimedFact]
    public async Task GetGroups_ComputesIsNew_WhenFirstSeenWithin24h()
    {
        var ctx = _fixture.CreateContext();
        var fresh = Group("fp-fresh", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop");
        fresh.FirstSeenAt = DateTime.UtcNow;
        var old = Group("fp-old", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop");
        old.FirstSeenAt = DateTime.UtcNow.AddDays(-2);
        ctx.Set<ErrorGroup>().AddRange(fresh, old);
        await ctx.SaveChangesAsync(Ct);

        var items = (await Service().GetGroups(null, null, null, null, 0, 50, Ct)).Items;

        items.Single(x => string.Equals(x.Fingerprint, "fp-fresh", StringComparison.Ordinal)).IsNew.ShouldBeTrue();
        items.Single(x => string.Equals(x.Fingerprint, "fp-old", StringComparison.Ordinal)).IsNew.ShouldBeFalse();
    }

    [TimedFact]
    public async Task GetGroups_ComputesIsRegressed_WhenUnresolvedWithStatusChanged()
    {
        var ctx = _fixture.CreateContext();
        var regressed = Group("fp-regressed", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop");
        regressed.StatusChangedAt = DateTime.UtcNow;   // re-opened after a resolve
        var plain = Group("fp-plain", ErrorSource.Job, ErrorGroupStatus.Unresolved, "shop");
        plain.StatusChangedAt = null;                  // never resolved
        ctx.Set<ErrorGroup>().AddRange(regressed, plain);
        await ctx.SaveChangesAsync(Ct);

        var items = (await Service().GetGroups(null, null, null, null, 0, 50, Ct)).Items;

        items.Single(x => string.Equals(x.Fingerprint, "fp-regressed", StringComparison.Ordinal)).IsRegressed.ShouldBeTrue();
        items.Single(x => string.Equals(x.Fingerprint, "fp-plain", StringComparison.Ordinal)).IsRegressed.ShouldBeFalse();
    }

    private static ErrorGroup Group(string fingerprint, ErrorSource source, ErrorGroupStatus status, string application) => new()
    {
        Fingerprint = fingerprint,
        Source = source,
        Kind = ErrorKind.Exception,
        ExceptionType = "System.NullReferenceException",
        Title = "boom",
        Culprit = "Acme.Orders.ProcessOrderRequest",
        Application = application,
        FirstSeenAt = DateTime.UtcNow,
        LastSeenAt = DateTime.UtcNow,
        Count = 1,
        Status = status,
    };

    private static Statistic Stat(string fingerprint, DateTime hourUtc, long value) => new()
    {
        Key = $"errorgroup:{fingerprint}:{hourUtc.ToUniversalTime().ToString("yyyyMMddHH", CultureInfo.InvariantCulture)}",
        Value = value,
    };
}

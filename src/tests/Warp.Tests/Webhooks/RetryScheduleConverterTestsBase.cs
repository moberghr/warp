using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Webhooks;

/// <summary>
/// <c>RetryScheduleConverter</c> roundtrip coverage (WSC1, §8.16 non-primitive persistence lesson): the
/// <c>IReadOnlyList&lt;TimeSpan&gt;</c> schedule survives a DB write + read identically for the empty list,
/// a single entry, and a multi-hour multi-entry span — on both providers.
/// </summary>
[GenerateDatabaseTests]
public abstract class RetryScheduleConverterTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected RetryScheduleConverterTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Roundtrip_EmptySchedule_PreservedAsEmpty()
    {
        var reloaded = await RoundtripAsync([]);

        reloaded.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task Roundtrip_SingleEntry_Preserved()
    {
        var schedule = new[] { TimeSpan.FromMinutes(1) };

        var reloaded = await RoundtripAsync(schedule);

        reloaded.ShouldBe(schedule);
    }

    [TimedFact]
    public async Task Roundtrip_MultiHourSpans_Preserved()
    {
        var schedule = new[]
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(6),
        };

        var reloaded = await RoundtripAsync(schedule);

        reloaded.ShouldBe(schedule);
    }

    private async Task<IReadOnlyList<TimeSpan>> RoundtripAsync(IReadOnlyList<TimeSpan> schedule)
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
            RetrySchedule = schedule,
            Status = WebhookDeliveryStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var reloaded = await _fixture.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        reloaded.ShouldNotBeNull();

        return reloaded.RetrySchedule;
    }
}

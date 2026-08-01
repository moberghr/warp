using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// <c>ExpirationCleanup</c> coverage for the client-event table (§8.27) — the browser-side mirror of the
/// endpoint call-log sweeps. Expired <see cref="ClientEventLog"/> rows are deleted past <c>ExpireAt</c>; the
/// count cap keeps the newest N rows per application by <c>Timestamp</c>. Each test drives one public method (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class ClientEventCleanupTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ClientEventCleanupTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Cleanup_Expired_Deleted()
    {
        await InsertAsync("shop", expireAt: DateTime.UtcNow.AddHours(-1));

        await CreateCleanup().CleanupExpiredClientEventLogsAsync(Ct);

        (await CountAsync("shop")).ShouldBe(0);
    }

    [TimedFact]
    public async Task Cleanup_Unexpired_Kept()
    {
        await InsertAsync("shop", expireAt: DateTime.UtcNow.AddHours(1));

        await CreateCleanup().CleanupExpiredClientEventLogsAsync(Ct);

        (await CountAsync("shop")).ShouldBe(1);
    }

    [TimedFact]
    public async Task Cleanup_ByCount_KeepsNewestPerApplication()
    {
        var basis = DateTime.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 5; i++)
        {
            await InsertAsync("shop", expireAt: null, timestamp: basis.AddSeconds(i));
        }

        await InsertAsync("other", expireAt: null, timestamp: basis);   // a different app is untouched by shop's cap

        var deleted = await CreateCleanup(retentionCount: 2).CleanupClientEventLogsByCountAsync(Ct);

        deleted.ShouldBe(3);                       // 5 → keep newest 2
        (await CountAsync("shop")).ShouldBe(2);
        (await CountAsync("other")).ShouldBe(1);   // independent app, its single row stays
    }

    [TimedFact]
    public async Task Cleanup_ByCount_NullCap_KeepsAll()
    {
        for (var i = 0; i < 4; i++)
        {
            await InsertAsync("shop", expireAt: null);
        }

        await CreateCleanup(retentionCount: null).CleanupClientEventLogsByCountAsync(Ct);

        (await CountAsync("shop")).ShouldBe(4);
    }

    private ExpirationCleanup<TestContext> CreateCleanup(int? retentionCount = null)
    {
        var configuration = new WarpServerConfiguration { ClientEventLogRetentionCount = retentionCount };

        return new ExpirationCleanup<TestContext>(
            new TestServerContext(_fixture.CreateContext()),
            TimeProvider.System,
            Options.Create(configuration),
            TestNotifiers.EmptyDispatcher());
    }

    private async Task InsertAsync(string application, DateTime? expireAt, DateTime? timestamp = null)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<ClientEventLog>().Add(new ClientEventLog
        {
            Application = application,
            Type = ClientEventType.Error,
            Timestamp = timestamp ?? DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            ExpireAt = expireAt,
        });

        await ctx.SaveChangesAsync(Ct);
    }

    private async Task<int> CountAsync(string application)
    {
        return await _fixture.CreateContext().Set<ClientEventLog>()
            .Where(x => x.Application == application)
            .CountAsync(Ct);
    }
}

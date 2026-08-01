using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core;
using Warp.Core.ClientObservability;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.ClientObservability;

/// <summary>
/// Persistence for client events (§8.27), driven through <see cref="ClientEventFlusher{TContext}.PersistBatchAsync"/>
/// directly (no background loop, §4.8) on both providers. Pins: the <see cref="ClientEventLog"/> row + the
/// durable <c>clientevent:</c> Counter fold, vital dur/histogram, ReceivedAt/ExpireAt stamping, and that the
/// cardinality guard collapses the COUNTER name while the stored row keeps the real name.
/// </summary>
[GenerateDatabaseTests]
public abstract class ClientEventFlusherTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ClientEventFlusherTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ClientEventCardinality Guard(int cap = 200) => new(cap, cap, cap);

    [TimedFact]
    public async Task Persist_Error_WritesRowAndCounters()
    {
        var record = new ClientEventRecord
        {
            Application = "shop",
            Type = ClientEventType.Error,
            Name = "TypeError",
            Message = "boom",
            Stack = "at x",
            Timestamp = new DateTime(2026, 7, 26, 8, 0, 0, DateTimeKind.Utc),
        };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(_fixture.CreateContext(), [record], Guard(), new WarpConfiguration(), TimeProvider.System, Ct);

        var ctx = _fixture.CreateContext();
        var row = await ctx.Set<ClientEventLog>().SingleAsync(Ct);
        row.Type.ShouldBe(ClientEventType.Error);
        row.Name.ShouldBe("TypeError");
        row.Application.ShouldBe("shop");

        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:total:error:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:name:error:TypeError:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent-app:shop:total:error:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_Vital_WritesDurationAndBucket()
    {
        var record = new ClientEventRecord
        {
            Application = "shop",
            Type = ClientEventType.Vital,
            Name = "LCP",
            Value = 2400,
            Timestamp = DateTime.UtcNow,
        };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(_fixture.CreateContext(), [record], Guard(), new WarpConfiguration(), TimeProvider.System, Ct);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:vital:LCP:dur").SumAsync(x => x.Value, Ct)).ShouldBe(2400);
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:vital:LCP:pct:2500").SumAsync(x => x.Value, Ct)).ShouldBe(1);
    }

    [TimedFact]
    public async Task Persist_StampsReceivedAtAndExpireAt()
    {
        var config = new WarpConfiguration { ClientEventLogRetention = TimeSpan.FromDays(3) };
        var record = new ClientEventRecord { Application = "shop", Type = ClientEventType.Log, Level = "warn", Timestamp = DateTime.UtcNow };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(_fixture.CreateContext(), [record], Guard(), config, TimeProvider.System, Ct);

        var row = await _fixture.CreateContext().Set<ClientEventLog>().SingleAsync(Ct);
        row.ExpireAt.ShouldNotBeNull();
        (row.ExpireAt!.Value - row.ReceivedAt).ShouldBe(TimeSpan.FromDays(3), tolerance: TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task Persist_CardinalityCollapse_CountersFoldButRowsKeepRealName()
    {
        var guard = Guard(cap: 1);   // one distinct error name, then {other}
        var first = new ClientEventRecord { Application = "shop", Type = ClientEventType.Error, Name = "TypeError", Timestamp = DateTime.UtcNow };
        var second = new ClientEventRecord { Application = "shop", Type = ClientEventType.Error, Name = "RangeError", Timestamp = DateTime.UtcNow };

        await ClientEventFlusher<TestContext>.PersistBatchAsync(_fixture.CreateContext(), [first, second], guard, new WarpConfiguration(), TimeProvider.System, Ct);

        var ctx = _fixture.CreateContext();

        // The counter for the second distinct name collapsed to {other}; the first kept its own key.
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:name:error:TypeError:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);
        (await ctx.Set<Counter>().Where(x => x.Key == "clientevent:name:error:{other}:count").SumAsync(x => x.Value, Ct)).ShouldBe(1);

        // ...but BOTH raw rows keep their real names (the collapse is metric-only).
        var names = await ctx.Set<ClientEventLog>().Select(x => x.Name).ToListAsync(Ct);
        names.ShouldContain("TypeError");
        names.ShouldContain("RangeError");
    }
}

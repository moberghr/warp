using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Observability;

/// <summary>
/// Database coverage for <see cref="CounterBufferFlusher{TContext}"/> — the writer that turns the
/// worker's in-memory <see cref="WarpCounterBuffer"/> increments into <c>Counter</c> rows.
/// <para>
/// The behaviour under test is the durability contract the buffering bought: increments live in
/// memory until a flush, so an ungraceful exit loses at most one interval — but a GRACEFUL stop must
/// lose nothing. That is the only part of the trade a user can actually rely on, and it is the part
/// a silent regression would make worthless.
/// </para>
/// </summary>
[GenerateDatabaseTests]
public abstract class CounterBufferFlusherTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected CounterBufferFlusherTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task FlushOnce_SumsEachKeyIntoASingleRow()
    {
        var buffer = new WarpCounterBuffer();
        for (var i = 0; i < 50; i++)
        {
            buffer.Add("stats:succeeded", 1);
            buffer.Add("stats:succeeded:2026-09-14-08", 1);
        }

        await CreateFlusher(buffer).FlushOnceAsync(Ct);

        var rows = await ReadCounters();

        // Two keys, one row each — not a hundred rows of one. That collapse is the entire point.
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(x => x.Value == 50);
    }

    [TimedFact]
    public async Task FlushOnce_WithNothingBuffered_TouchesTheDatabaseNotAtAll()
    {
        var buffer = new WarpCounterBuffer();

        (await CreateFlusher(buffer).FlushOnceAsync(Ct)).ShouldBe(0);

        (await ReadCounters()).ShouldBeEmpty();
    }

    [TimedFact]
    public async Task FlushOnce_DrainsTheBuffer_SoASecondFlushWritesNothing()
    {
        var buffer = new WarpCounterBuffer();
        buffer.Add("stats:failed", 3);

        await CreateFlusher(buffer).FlushOnceAsync(Ct);
        await CreateFlusher(buffer).FlushOnceAsync(Ct);

        var rows = await ReadCounters();
        rows.ShouldHaveSingleItem().Value.ShouldBe(3);
    }

    /// <summary>
    /// The graceful-shutdown half of the durability contract: stopping the host must write out what
    /// is still buffered, so an orderly stop loses nothing. Only an ungraceful exit is allowed to
    /// lose an interval.
    /// </summary>
    [TimedFact]
    public async Task StopAsync_FlushesWhatIsStillBuffered()
    {
        var buffer = new WarpCounterBuffer();
        var flusher = CreateFlusher(buffer);

        await flusher.StartAsync(Ct);
        buffer.Add("stats:succeeded", 7);

        await flusher.StopAsync(Ct);

        (await ReadCounters()).ShouldHaveSingleItem().Value.ShouldBe(7);
    }

    /// <summary>
    /// Counter.Value is an int while the buffer accumulates a long, so a key that summed past
    /// int.MaxValue within one interval must be split across rows rather than truncated. Counters are
    /// additive, so the fold produces the same total either way — a silent truncation would not.
    /// </summary>
    [TimedFact]
    public async Task FlushOnce_ValueBeyondIntMax_SplitsAcrossRowsWithoutLosingTheTotal()
    {
        var buffer = new WarpCounterBuffer();
        buffer.Add("jobstat:type:x:dur", (long)int.MaxValue + 25);

        await CreateFlusher(buffer).FlushOnceAsync(Ct);

        var rows = await ReadCounters();
        rows.Count.ShouldBe(2);
        rows.Sum(x => (long)x.Value).ShouldBe((long)int.MaxValue + 25);
    }

    /// <summary>
    /// A failed write must hand the increments back rather than drop them. The drain has already
    /// emptied the buffer by the time the write runs, so without this a transient connection reset in
    /// a perfectly healthy process loses that interval outright — well outside the documented trade,
    /// which only allows an UNGRACEFUL exit to lose one. Counters are additive, so returning them is
    /// exact: the next flush writes this interval summed with the following one.
    /// </summary>
    [TimedFact]
    public async Task FlushOnce_WhenTheWriteFails_ReturnsTheIncrementsToTheBuffer()
    {
        var buffer = new WarpCounterBuffer();
        buffer.Add("stats:succeeded", 4);
        buffer.Add("stats:failed", 1);

        // A disposed context fails the write without needing a broken database.
        var broken = _fixture.CreateContext();
        await broken.DisposeAsync();

        await Should.ThrowAsync<Exception>(async () =>
            await CreateFlusher(buffer, broken).FlushOnceAsync(Ct));

        // Nothing written, and nothing lost: the next flush carries the full total.
        (await ReadCounters()).ShouldBeEmpty();
        await CreateFlusher(buffer).FlushOnceAsync(Ct);

        var rows = await ReadCounters();
        rows.Count.ShouldBe(2);
        rows.Single(x => string.Equals(x.Key, "stats:succeeded", StringComparison.Ordinal)).Value.ShouldBe(4);
        rows.Single(x => string.Equals(x.Key, "stats:failed", StringComparison.Ordinal)).Value.ShouldBe(1);
    }

    private async Task<List<Counter>> ReadCounters() =>
        await _fixture.CreateContext()
            .Set<Counter>()
            .AsNoTracking()
            .ToListAsync(Ct);

    private CounterBufferFlusher<TestContext> CreateFlusher(WarpCounterBuffer buffer, DbContext? context = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IWarpServerContext>(_ => new TestServerContext(context ?? _fixture.CreateContext()));
        var provider = services.BuildServiceProvider();

        return new CounterBufferFlusher<TestContext>(
            provider.GetRequiredService<IServiceScopeFactory>(),
            buffer,
            Options.Create(new WarpServerConfiguration()),
            NullLogger<CounterBufferFlusher<TestContext>>.Instance);
    }
}

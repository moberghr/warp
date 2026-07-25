using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Adapters;

/// <summary>
/// DB-persistence coverage for the adapter recorder + flusher (SC1, SC3 persisted metadata shape).
/// Drives real scopes through <see cref="DbAdapterCallRecorder"/> so the <c>RecordCalls</c> gating is
/// exercised end-to-end, then persists the drained batch via
/// <see cref="AdapterCallFlusher{TContext}.PersistBatchAsync"/> against the fixture context (§4.8).
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterRecorderTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected AdapterRecorderTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Fail_PersistsFailedRow_WithExceptionDetails()
    {
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        row.AdapterName.ShouldBe("vendor");
        row.Operation.ShouldBe("GetOrders");
        row.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        row.ExceptionMessage.ShouldBe("boom");
        row.MachineName.ShouldNotBeNullOrEmpty();
    }

    [TimedFact]
    public async Task Succeed_DefaultRecordAll_PersistsSuccessRow()
    {
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task Succeed_FailuresOnly_PersistsNoRow()
    {
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var count = await _fixture.CreateContext().Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        count.ShouldBe(0);
    }

    [TimedFact]
    public async Task Succeed_FailuresOnly_WritesSuccessCounter_ButNoLogRow()
    {
        var options = new WarpAdapterOptions { RecordCalls = CallRecording.FailuresOnly };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var logCount = await _fixture.CreateContext().Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        logCount.ShouldBe(0);

        var successCounter = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == AdapterCounterKeys.Total("vendor", "success"))
            .SumAsync(x => (long)x.Value, Xunit.TestContext.Current.CancellationToken);
        successCounter.ShouldBe(1);
    }

    [TimedFact]
    public async Task Succeed_SampleRateZero_PersistsNoRow()
    {
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var count = await _fixture.CreateContext().Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        count.ShouldBe(0);
    }

    [TimedFact]
    public async Task Fail_SampleRateZero_PersistsRow()
    {
        // Sampling never drops failures — the row is the point of a failure.
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Fail(new InvalidOperationException("boom"));

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.Outcome.ShouldBe(AdapterCallOutcome.Failed);
    }

    [TimedFact]
    public async Task Succeed_SampleRateZero_MultipleCalls_CountersStayExact_NoRows()
    {
        // The volume knob suppresses ROWS only: N successes at SampleRate=0 write no log rows but the
        // success COUNT and duration-SUM counters still reflect every call (aggregates stay 100% exact).
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();
        adapters.BeginCall("vendor", "GetOrders").Succeed();
        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var logCount = await _fixture.CreateContext().Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken);
        logCount.ShouldBe(0);

        var successCounter = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == AdapterCounterKeys.Total("vendor", "success"))
            .SumAsync(x => (long)x.Value, Xunit.TestContext.Current.CancellationToken);
        successCounter.ShouldBe(3);
    }

    [TimedFact]
    public async Task Succeed_ForceCapture_SampleRateZero_PersistsRow()
    {
        var options = new WarpAdapterOptions { SampleRate = 0.0 };
        var (adapters, recorder, registry) = CreateStack(options);

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetForceCapture(true);
        scope.Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task Persist_TagsCorrelationAndGroup_RecordedOnRow()
    {
        var (adapters, recorder, registry) = CreateStack();

        var scope = adapters.BeginCall("vendor", "GetOrders", "shop-eu");
        scope.SetTag("region", "eu");
        scope.SetCorrelation("delivery-42");
        scope.Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.GroupName.ShouldBe("shop-eu");
        row.CorrelationId.ShouldBe("delivery-42");
        row.TagsJson.ShouldNotBeNull();
        row.TagsJson.ShouldContain("region");
        row.TagsJson.ShouldContain("eu");
    }

    [TimedFact]
    public async Task Persist_StampsExpireAt_FromGlobalRetention()
    {
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry, new WarpConfiguration { AdapterCallLogRetention = TimeSpan.FromDays(3) });

        var row = await SingleRowAsync();
        row.ExpireAt.ShouldNotBeNull();
        row.ExpireAt.Value.ShouldBe(row.Timestamp.AddDays(3), TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task Persist_PerAdapterRetentionOverride_WinsOverGlobal()
    {
        var options = new WarpAdapterOptions { CallLogRetention = TimeSpan.FromDays(1) };
        var (adapters, recorder, registry) = CreateStack(options);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry, new WarpConfiguration { AdapterCallLogRetention = TimeSpan.FromDays(30) });

        var row = await SingleRowAsync();
        row.ExpireAt.ShouldNotBeNull();
        row.ExpireAt.Value.ShouldBe(row.Timestamp.AddDays(1), TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task Persist_CaptureFields_LandInDedicatedColumns()
    {
        var (adapters, recorder, registry) = CreateStack();

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetRequestSummary("GET /orders/{id}");
        scope.SetStatusCode(200);

        // Values arrive already redacted + truncated from the transport binding; the recorder stores them verbatim.
        scope.SetRequestHeaders("Authorization: ***\nAccept: application/json");
        scope.SetResponseHeaders("Content-Type: application/json");
        scope.SetRequestBody("{\"id\":42}");
        scope.SetResponseBody("{\"ok\":true}");
        scope.Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.RequestSummary.ShouldBe("GET /orders/{id}");
        row.StatusCode.ShouldBe(200);
        row.RequestHeaders.ShouldBe("Authorization: ***\nAccept: application/json");
        row.ResponseHeaders.ShouldBe("Content-Type: application/json");
        row.RequestBody.ShouldBe("{\"id\":42}");
        row.ResponseBody.ShouldBe("{\"ok\":true}");
    }

    [TimedFact]
    public async Task Persist_NoCapture_LeavesCaptureColumnsNull()
    {
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.RequestSummary.ShouldBeNull();
        row.StatusCode.ShouldBeNull();
        row.RequestHeaders.ShouldBeNull();
        row.ResponseHeaders.ShouldBeNull();
        row.RequestBody.ShouldBeNull();
        row.ResponseBody.ShouldBeNull();
    }

    [TimedFact]
    public async Task Persist_FirstSight_CreatesAdapterDefinition()
    {
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("fresh-vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var definition = await _fixture.CreateContext().Set<AdapterDefinition>()
            .Where(x => x.Name == "fresh-vendor")
            .FirstOrDefaultAsync(Xunit.TestContext.Current.CancellationToken);

        definition.ShouldNotBeNull();
        definition.FirstSeenAt.ShouldBe(definition.LastSeenAt);
    }

    [TimedFact]
    public async Task Persist_ExistingBareDefinition_BackfillsConfigSummaryAndGroupLabel()
    {
        // The rate limiter can create an AdapterDefinition row first — carrying its shared policy but NO
        // ConfigSummary / GroupLabel (those come from the recording registry). A later flush for that adapter
        // must backfill both from the registry onto the existing row, not only stamp them on a fresh insert.
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = "vendor",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-10),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10),
            SharedPolicyJson = AdapterSharedPolicy.ToJson(5, 60),
            SharedPolicyHash = AdapterSharedPolicy.Hash(5, 60),
            ConfigSummary = null,
            GroupLabel = null,
        });
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var registry = new AdapterRegistry();
        registry.Register("vendor", new WarpAdapterOptions { GroupLabel = "Endpoint" }, configSummary: "caps=Always");
        var recorder = new DbAdapterCallRecorder();
        var adapters = new WarpAdapters(registry, recorder, TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var definition = await _fixture.CreateContext().Set<AdapterDefinition>()
            .Where(x => x.Name == "vendor")
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        definition.ConfigSummary.ShouldBe("caps=Always");
        definition.GroupLabel.ShouldBe("Endpoint");

        // The pre-existing policy is untouched by the backfill — only the display fields are filled in.
        definition.SharedPolicyHash.ShouldBe(AdapterSharedPolicy.Hash(5, 60));
    }

    [TimedFact]
    public async Task Persist_RecentDefinition_SkipsLastSeenAtRewrite()
    {
        // The "lazy" LastSeenAt refresh must actually SKIP the write while the row is inside the 5-min
        // stale threshold — if it silently degraded to unconditional writes, every flush would dirty the
        // definition row and the grace/threshold invariant (see AdapterDefinitionOrphanGrace doc) would
        // rest on a dead optimisation.
        var recentSeen = DateTime.UtcNow.AddSeconds(-30);
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = "vendor",
            FirstSeenAt = recentSeen,
            LastSeenAt = recentSeen,
            ConfigSummary = "caps=None",
            GroupLabel = "Group",
        });
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (adapters, recorder, registry) = CreateStack();
        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var definition = await _fixture.CreateContext().Set<AdapterDefinition>()
            .Where(x => x.Name == "vendor")
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        definition.LastSeenAt.ShouldBe(recentSeen, TimeSpan.FromSeconds(1));
    }

    [TimedFact]
    public async Task Persist_StaleDefinition_RefreshesLastSeenAt()
    {
        // The companion direction: past the 5-min threshold the flush must refresh LastSeenAt, or an
        // active adapter would drift into the orphan-grace window and be deleted mid-use.
        var staleSeen = DateTime.UtcNow.AddMinutes(-6);
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = "vendor",
            FirstSeenAt = staleSeen,
            LastSeenAt = staleSeen,
            ConfigSummary = "caps=None",
            GroupLabel = "Group",
        });
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var (adapters, recorder, registry) = CreateStack();
        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var definition = await _fixture.CreateContext().Set<AdapterDefinition>()
            .Where(x => x.Name == "vendor")
            .FirstAsync(Xunit.TestContext.Current.CancellationToken);

        definition.LastSeenAt.ShouldBeGreaterThan(DateTime.UtcNow.AddMinutes(-1));
    }

    [TimedFact]
    public async Task Persist_MultiAdapterBatch_UpsertsEveryDefinitionAndRow()
    {
        // One flush batch carrying records for several DISTINCT adapters (a mix of new and existing) —
        // the bulk definition lookup + per-name upsert loop was previously only ever exercised with a
        // single adapter per batch.
        var seed = _fixture.CreateContext();
        seed.Set<AdapterDefinition>().Add(new AdapterDefinition
        {
            Name = "vendor-existing",
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-10),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10),
        });
        await seed.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var registry = new AdapterRegistry();
        var recorder = new DbAdapterCallRecorder();
        var adapters = new WarpAdapters(registry, recorder, TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);

        adapters.BeginCall("vendor-a", "GetOrders").Succeed();
        adapters.BeginCall("vendor-b", "GetOrders").Succeed();
        adapters.BeginCall("vendor-existing", "GetOrders").Succeed();

        await FlushAsync(recorder, registry);

        var ctx = _fixture.CreateContext();
        (await ctx.Set<AdapterCallLog>().CountAsync(Xunit.TestContext.Current.CancellationToken)).ShouldBe(3);

        var names = await ctx.Set<AdapterDefinition>()
            .Select(x => x.Name)
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        names.ShouldContain("vendor-a");
        names.ShouldContain("vendor-b");
        names.ShouldContain("vendor-existing");
    }

    [TimedFact]
    public async Task Persist_OverLongCorrelationAndGroup_ClampedToColumnCaps()
    {
        // Same choke point as the operation clamp, for the two other caller-supplied 200-cap columns.
        var (adapters, recorder, registry) = CreateStack();

        var scope = adapters.BeginCall("vendor", "GetOrders");
        scope.SetCorrelation(new string('c', 500));
        scope.SetGroup(new string('g', 500));
        scope.Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.CorrelationId.ShouldNotBeNull().Length.ShouldBe(200);
        row.GroupName.ShouldNotBeNull().Length.ShouldBe(200);
    }

    [TimedFact]
    public async Task Persist_OverLongOperation_ClampedToColumnCap()
    {
        // The scope is the single choke point: an over-long value clamps to its column cap before the
        // record is handed over, so the batch insert never fails (F3).
        var (adapters, recorder, registry) = CreateStack();

        adapters.BeginCall("vendor", new string('x', 5000)).Succeed();

        await FlushAsync(recorder, registry);

        var row = await SingleRowAsync();
        row.Operation.Length.ShouldBe(200);
    }

    [TimedFact]
    public async Task PersistWithFallback_PoisonRecord_DoesNotBlockSiblingsOrCounters()
    {
        // A directly-built record bypasses the scope clamp to simulate a poison row (over-long operation).
        // The batch SaveChanges fails; the fallback re-persists per record so the healthy sibling — row and
        // counters — still lands while only the poison row is dropped (F3).
        var poison = MakeRecord("poison-vendor", new string('p', 5000));
        var good = MakeRecord("good-vendor", "GetOk");

        await AdapterCallFlusher<TestContext>.PersistWithFallbackAsync(
            _fixture.CreateContext,
            [poison, good],
            new AdapterRegistry(),
            new WarpConfiguration(),
            TimeProvider.System,
            NullLogger.Instance,
            Xunit.TestContext.Current.CancellationToken);

        var goodRows = await _fixture.CreateContext().Set<AdapterCallLog>()
            .CountAsync(x => x.AdapterName == "good-vendor", Xunit.TestContext.Current.CancellationToken);
        goodRows.ShouldBe(1);

        var poisonRows = await _fixture.CreateContext().Set<AdapterCallLog>()
            .CountAsync(x => x.AdapterName == "poison-vendor", Xunit.TestContext.Current.CancellationToken);
        poisonRows.ShouldBe(0);

        var goodCounter = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == AdapterCounterKeys.Total("good-vendor", "success"))
            .SumAsync(x => (long)x.Value, Xunit.TestContext.Current.CancellationToken);
        goodCounter.ShouldBe(1);
    }

    [TimedFact]
    public async Task StopAsync_DrainsBufferedRecords_OnShutdown()
    {
        // BUG-4: on shutdown the base cancels stoppingToken, which used to break the drain loop and discard
        // records still buffered in the channel — leaving Delivered deliveries with missing final attempt rows.
        // StopAsync now completes the writer then drains the buffered records on CancellationToken.None.
        var recorder = new DbAdapterCallRecorder();
        recorder.Record(MakeRecord("shutdown-vendor", "GetOrders"));
        recorder.Record(MakeRecord("shutdown-vendor", "GetItems"));

        var flusher = new AdapterCallFlusher<TestContext>(
            recorder,
            new FixtureScopeFactory(_fixture.CreateContext),
            new AdapterRegistry(),
            TimeProvider.System,
            Options.Create(new WarpConfiguration()),
            NullLogger<AdapterCallFlusher<TestContext>>.Instance);

        // Never started: the records sit buffered exactly as they would at a shutdown that beats the drain loop.
        await flusher.StopAsync(Xunit.TestContext.Current.CancellationToken);

        var rows = await _fixture.CreateContext().Set<AdapterCallLog>()
            .CountAsync(x => x.AdapterName == "shutdown-vendor", Xunit.TestContext.Current.CancellationToken);
        rows.ShouldBe(2);
    }

    private static AdapterCallRecord MakeRecord(string adapter, string operation)
        => new()
        {
            AdapterName = adapter,
            Operation = operation,
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        };

    private static (WarpAdapters Adapters, DbAdapterCallRecorder Recorder, AdapterRegistry Registry) CreateStack(
        WarpAdapterOptions? options = null,
        string adapterName = "vendor")
    {
        var registry = new AdapterRegistry();
        if (options is not null)
        {
            registry.Register(adapterName, options);
        }

        var recorder = new DbAdapterCallRecorder();
        var adapters = new WarpAdapters(registry, recorder, TimeProvider.System, NullLogger<WarpAdapters>.Instance, []);

        return (adapters, recorder, registry);
    }

    private async Task FlushAsync(DbAdapterCallRecorder recorder, AdapterRegistry registry, WarpConfiguration? configuration = null)
    {
        var batch = new List<AdapterCallRecord>();
        while (recorder.Reader.TryRead(out var record))
        {
            batch.Add(record);
        }

        await AdapterCallFlusher<TestContext>.PersistBatchAsync(
            _fixture.CreateContext(),
            batch,
            registry,
            configuration ?? new WarpConfiguration(),
            TimeProvider.System,
            Xunit.TestContext.Current.CancellationToken);
    }

    private async Task<AdapterCallLog> SingleRowAsync()
    {
        var rows = await _fixture.CreateContext().Set<AdapterCallLog>()
            .ToListAsync(Xunit.TestContext.Current.CancellationToken);

        return rows.ShouldHaveSingleItem();
    }

    // Minimal IServiceScopeFactory that resolves TestContext from the fixture — lets the flusher's
    // per-batch scope creation (§0.5) run against the fixture database without a full DI container.
    private sealed class FixtureScopeFactory : IServiceScopeFactory
    {
        private readonly Func<TestContext> _createContext;

        public FixtureScopeFactory(Func<TestContext> createContext) => _createContext = createContext;

        public IServiceScope CreateScope() => new FixtureScope(_createContext());

        private sealed class FixtureScope : IServiceScope, IServiceProvider
        {
            private readonly TestContext _context;

            public FixtureScope(TestContext context) => _context = context;

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) => serviceType == typeof(TestContext) ? _context : null;

            public void Dispose() => _context.Dispose();
        }
    }
}

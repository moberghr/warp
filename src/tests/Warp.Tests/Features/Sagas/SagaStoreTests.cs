using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Notifications;
using Warp.Core.Sagas;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;

namespace Warp.Tests.Features.Sagas;

[GenerateDatabaseTests]
public abstract class SagaStoreTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected SagaStoreTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task Load_UnknownKey_ReturnsNull()
    {
        var store = new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);

        var saga = await store.Load<TestSaga>("never-existed", TestCancellation);

        saga.ShouldBeNull();
    }

    [TimedFact]
    public async Task Load_ExistingSaga_ReturnsTypedInstance()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "abc",
            StateJson = JsonSerializer.Serialize(new TestSaga { CorrelationKey = "abc", Counter = 7 }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(TestCancellation);

        var store = new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var saga = await store.Load<TestSaga>("abc", TestCancellation);

        saga.ShouldNotBeNull();
        saga.CorrelationKey.ShouldBe("abc");
        saga.Counter.ShouldBe(7);
    }

    [TimedFact]
    public async Task Add_NewSaga_PersistsWithVersionSet()
    {
        var ctx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(ctx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);

        var saga = new TestSaga { CorrelationKey = "new", Counter = 1 };
        store.Add(saga);
        await store.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var row = await readCtx.Set<SagaState>()
            .Where(x => x.CorrelationKey == "new")
            .FirstOrDefaultAsync(TestCancellation);

        row.ShouldNotBeNull();
        row.Type.ShouldBe(typeof(TestSaga).FullName);
        row.Version.ShouldNotBe(Guid.Empty);
        row.CreatedAt.ShouldBeGreaterThan(default);
        row.UpdatedAt.ShouldBeGreaterThan(default);
    }

    [TimedFact]
    public async Task Update_ExistingSaga_BumpsVersion()
    {
        var sagaId = Guid.NewGuid();
        var initialVersion = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "v",
            StateJson = JsonSerializer.Serialize(new TestSaga { CorrelationKey = "v", Counter = 1 }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = initialVersion,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var actCtx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(actCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var loaded = await store.Load<TestSaga>("v", TestCancellation);
        loaded!.Counter = 99;
        store.Update(loaded);
        await store.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var row = await readCtx.Set<SagaState>().FindAsync([sagaId], TestCancellation);
        row.ShouldNotBeNull();
        row.Version.ShouldNotBe(initialVersion);
        JsonSerializer.Deserialize<TestSaga>(row.StateJson)!.Counter.ShouldBe(99);
    }

    [TimedFact]
    public async Task Remove_ExistingSaga_DeletesRow()
    {
        var sagaId = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "rm",
            StateJson = JsonSerializer.Serialize(new TestSaga { CorrelationKey = "rm" }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var actCtx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(actCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var loaded = await store.Load<TestSaga>("rm", TestCancellation);
        store.Remove(loaded!);
        await store.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var row = await readCtx.Set<SagaState>().FindAsync([sagaId], TestCancellation);
        row.ShouldBeNull();
    }

    [TimedFact]
    public async Task UniqueIndex_TwoSagasSameTypeAndKey_SecondInsertThrows()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "dup",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var actCtx = _fixture.CreateContext();
        actCtx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "dup",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await Should.ThrowAsync<DbUpdateException>(() => actCtx.SaveChangesAsync(TestCancellation));
    }

    [TimedFact]
    public async Task UniqueIndex_TwoSagasSameKeyDifferentTypes_BothCoexist()
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "shared",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(OtherTestSaga).FullName!,
            CorrelationKey = "shared",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var rows = await readCtx.Set<SagaState>()
            .Where(x => x.CorrelationKey == "shared")
            .ToListAsync(TestCancellation);
        rows.Count.ShouldBe(2);
    }

    [TimedFact]
    public async Task UniqueIndex_KeyReusableAfterRemoval()
    {
        var firstId = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = firstId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "reuse",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var removeCtx = _fixture.CreateContext();
        var existing = await removeCtx.Set<SagaState>().FindAsync([firstId], TestCancellation)
            ?? throw new InvalidOperationException("arrange row missing");
        removeCtx.Set<SagaState>().Remove(existing);
        await removeCtx.SaveChangesAsync(TestCancellation);

        var secondId = Guid.NewGuid();
        var actCtx = _fixture.CreateContext();
        actCtx.Set<SagaState>().Add(new SagaState
        {
            Id = secondId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "reuse",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await actCtx.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var live = await readCtx.Set<SagaState>()
            .Where(x => x.CorrelationKey == "reuse")
            .ToListAsync(TestCancellation);
        live.Count.ShouldBe(1);
        live[0].Id.ShouldBe(secondId);
    }

    [TimedFact]
    public async Task Load_StateJsonHasUnmappedField_LoadsCleanly_DropsUnknown()
    {
        // Schema-evolution scenario: a previous version of the saga had a "RemovedField"
        // property; today's saga subclass no longer declares it. SagaStore.Load uses
        // UnmappedMemberHandling.Skip so the persisted row loads with the known fields
        // populated and the unknown field silently dropped — the alternative (throwing) would
        // break every persisted saga on any property rename/removal in user code.
        var ctx = _fixture.CreateContext();
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = Guid.NewGuid(),
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "schema-evo",
            StateJson = """{"Counter": 9, "RemovedField": "old-data"}""",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(TestCancellation);

        var store = new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var loaded = await store.Load<TestSaga>("schema-evo", TestCancellation);

        loaded.ShouldNotBeNull();
        loaded.Counter.ShouldBe(9);
    }

    [TimedFact]
    public async Task Load_StateJsonMissingNewProperty_LoadsWithDefault_AndPersistsAfterUpdate()
    {
        // Reverse schema-evolution scenario: an old row was written without a property that
        // the current TestSaga class now declares (e.g. `Counter`). System.Text.Json's
        // default for an absent member is the property's default value. After a handler
        // updates the property, the next Update + SaveChanges must persist it; the next
        // Load must read it back. Closes the documented additive direction of the policy.
        var sagaId = Guid.NewGuid();
        var ctx = _fixture.CreateContext();
        ctx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "additive",
            StateJson = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(TestCancellation);

        var actCtx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(actCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var loaded = await store.Load<TestSaga>("additive", TestCancellation);
        loaded.ShouldNotBeNull();
        loaded.Counter.ShouldBe(0); // default — absent from JSON

        loaded.Counter = 77;
        store.Update(loaded);
        await store.SaveChangesAsync(TestCancellation);

        var verifyCtx = _fixture.CreateContext();
        var roundTripStore = new SagaStore<TestContext>(verifyCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var reloaded = await roundTripStore.Load<TestSaga>("additive", TestCancellation);
        reloaded.ShouldNotBeNull();
        reloaded.Counter.ShouldBe(77);
    }

    [TimedFact]
    public async Task Add_GuidKeyedSagaSubclass_StoresOnlySubclassStateInJson_AndRoundTrips()
    {
        var orderId = Guid.NewGuid();
        var canonical = orderId.ToString("N");

        var actCtx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(actCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var saga = new GuidKeyedSaga { Counter = 5, Key = orderId };
        store.Add(saga);
        await store.SaveChangesAsync(TestCancellation);

        var readCtx = _fixture.CreateContext();
        var row = await readCtx.Set<SagaState>()
            .Where(x => x.CorrelationKey == canonical)
            .Where(x => x.Type == typeof(GuidKeyedSaga).FullName)
            .FirstOrDefaultAsync(TestCancellation);
        row.ShouldNotBeNull();

        // StateJson must contain ONLY the subclass's own state — the base class properties (Id,
        // CorrelationKey, IsCompleted) and the Saga<TKey>.Key projection are all [JsonIgnore]'d
        // because their authoritative storage is the SagaState row's columns (the store reassigns
        // them on Load). The user's typed Key (Guid) round-trips via the persisted CorrelationKey
        // column, not via JSON.
        row.StateJson.ShouldNotContain("\"Key\"");
        row.StateJson.ShouldNotContain("\"CorrelationKey\"");
        row.StateJson.ShouldNotContain("\"Id\"");
        row.StateJson.ShouldNotContain("\"IsCompleted\"");
        row.StateJson.ShouldContain("\"Counter\"");

        // Re-load through SagaStore to confirm typed Key access reads back the Guid intact.
        var reloadCtx = _fixture.CreateContext();
        var reloadStore = new SagaStore<TestContext>(reloadCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var loaded = await reloadStore.Load<GuidKeyedSaga>(canonical, TestCancellation);
        loaded.ShouldNotBeNull();
        loaded.Key.ShouldBe(orderId);
        loaded.Counter.ShouldBe(5);
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;

    private sealed class TestSaga : Saga
    {
        public int Counter { get; set; }
    }

    private sealed class OtherTestSaga : Saga;

    private sealed class GuidKeyedSaga : Saga<Guid>
    {
        public int Counter { get; set; }
    }

    [TimedFact]
    public async Task Add_IntKeyedSagaSubclass_RoundTripsThroughDb()
    {
        var orderId = 4242;
        var canonical = orderId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var store = new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var saga = new IntKeyedSaga { Counter = 7, Key = orderId };
        store.Add(saga);
        await store.SaveChangesAsync(TestCancellation);

        var row = await _fixture.CreateContext().Set<SagaState>()
            .Where(x => x.CorrelationKey == canonical && x.Type == typeof(IntKeyedSaga).FullName)
            .FirstOrDefaultAsync(TestCancellation);
        row.ShouldNotBeNull();
        row.StateJson.ShouldNotContain("\"Key\"");

        var loaded = await new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals).Load<IntKeyedSaga>(canonical, TestCancellation);
        loaded.ShouldNotBeNull();
        loaded.Key.ShouldBe(orderId);
        loaded.Counter.ShouldBe(7);
    }

    [TimedFact]
    public async Task Add_LongKeyedSagaSubclass_RoundTripsThroughDb()
    {
        var orderId = 9_000_000_000L;
        var canonical = orderId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var store = new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var saga = new LongKeyedSaga { Counter = 11, Key = orderId };
        store.Add(saga);
        await store.SaveChangesAsync(TestCancellation);

        var row = await _fixture.CreateContext().Set<SagaState>()
            .Where(x => x.CorrelationKey == canonical && x.Type == typeof(LongKeyedSaga).FullName)
            .FirstOrDefaultAsync(TestCancellation);
        row.ShouldNotBeNull();
        row.StateJson.ShouldNotContain("\"Key\"");

        var loaded = await new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals).Load<LongKeyedSaga>(canonical, TestCancellation);
        loaded.ShouldNotBeNull();
        loaded.Key.ShouldBe(orderId);
        loaded.Counter.ShouldBe(11);
    }

    private sealed class IntKeyedSaga : Saga<int>
    {
        public int Counter { get; set; }
    }

    private sealed class LongKeyedSaga : Saga<long>
    {
        public int Counter { get; set; }
    }

    [TimedFact]
    public async Task Update_MultipleSaves_VersionChangesEachTime()
    {
        // Three updates → three distinct versions. The prior Update_ExistingSaga_BumpsVersion
        // only proved v1 != v2; this proves the version is truly fresh on every save, not
        // just toggling between two values.
        var sagaId = Guid.NewGuid();
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<SagaState>().Add(new SagaState
        {
            Id = sagaId,
            Type = typeof(TestSaga).FullName!,
            CorrelationKey = "v-mono",
            StateJson = JsonSerializer.Serialize(new TestSaga { CorrelationKey = "v-mono", Counter = 0 }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Version = Guid.NewGuid(),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var versions = new List<Guid>();
        for (var i = 1; i <= 3; i++)
        {
            var ctx = _fixture.CreateContext();
            var store = new SagaStore<TestContext>(ctx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
            var loaded = await store.Load<TestSaga>("v-mono", TestCancellation);
            loaded!.Counter = i;
            store.Update(loaded);
            await store.SaveChangesAsync(TestCancellation);

            var version = await _fixture.CreateContext().Set<SagaState>()
                .Where(x => x.Id == sagaId)
                .Select(x => x.Version)
                .FirstAsync(TestCancellation);
            versions.Add(version);
        }

        versions.Distinct().Count().ShouldBe(3, "each update should bump Version to a fresh Guid");
    }

    [TimedFact]
    public async Task RoundTrip_ComplexStateShape_PreservesAllFields()
    {
        // User-state fidelity check: nested object, list, nullable, UTC DateTime, enum, Guid.
        // If JSON serialization ever changes (e.g. someone adds custom options), this catches
        // round-trip corruption for the kinds of types real sagas hold.
        var arrangeCtx = _fixture.CreateContext();
        var store = new SagaStore<TestContext>(arrangeCtx, TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals);
        var saga = new ComplexSaga
        {
            CorrelationKey = "complex",
            Title = "Order #42",
            ItemCount = 5,
            Total = 199.99m,
            PlacedAt = new DateTime(2026, 5, 13, 12, 30, 45, DateTimeKind.Utc),
            Tags = ["priority", "fragile"],
            Customer = new CustomerInfo { Id = Guid.Parse("11112222-3333-4444-5555-666677778888"), Tier = CustomerTier.Gold },
            CompletedAt = null,
        };
        store.Add(saga);
        await store.SaveChangesAsync(TestCancellation);

        var loaded = await new SagaStore<TestContext>(_fixture.CreateContext(), TimeProvider.System, new NullNotificationTransport(), new FakeExceptionClassifier(), TestTasks.NullSignals)
            .Load<ComplexSaga>("complex", TestCancellation);

        loaded.ShouldNotBeNull();
        loaded.Title.ShouldBe("Order #42");
        loaded.ItemCount.ShouldBe(5);
        loaded.Total.ShouldBe(199.99m);
        loaded.PlacedAt.ShouldBe(new DateTime(2026, 5, 13, 12, 30, 45, DateTimeKind.Utc));
        loaded.PlacedAt.Kind.ShouldBe(DateTimeKind.Utc);
        loaded.Tags.ShouldBe(["priority", "fragile"]);
        loaded.Customer.ShouldNotBeNull();
        loaded.Customer.Id.ShouldBe(Guid.Parse("11112222-3333-4444-5555-666677778888"));
        loaded.Customer.Tier.ShouldBe(CustomerTier.Gold);
        loaded.CompletedAt.ShouldBeNull();
    }

    private sealed class ComplexSaga : Saga
    {
        public string Title { get; set; } = string.Empty;

        public int ItemCount { get; set; }

        public decimal Total { get; set; }

        public DateTime PlacedAt { get; set; }

        public List<string> Tags { get; set; } = [];

        public CustomerInfo? Customer { get; set; }

        public DateTime? CompletedAt { get; set; }
    }

    private sealed class CustomerInfo
    {
        public Guid Id { get; set; }

        public CustomerTier Tier { get; set; }
    }

    private enum CustomerTier
    {
        Bronze = 1,
        Silver = 2,
        Gold = 3,
    }
}

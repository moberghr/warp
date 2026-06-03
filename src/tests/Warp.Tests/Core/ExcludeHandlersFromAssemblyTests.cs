using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Handlers;
using Warp.Provider.PostgreSql;
using Warp.Tests.Http;
using Warp.Worker;

namespace Warp.Tests.Core;

/// <summary>
/// Confirms <c>opt.ExcludeHandlersFromAssembly(...)</c> removes IRequestHandler /
/// IJobHandler / IMessageHandler / IStreamRequestHandler registrations whose
/// implementation type lives in the excluded assembly. Multi-host solutions hit
/// scope-validation failures otherwise: the source generator scans referenced
/// assemblies transitively, so a worker-only handler with worker-only dependencies
/// ends up registered in an API host's DI graph (issue raised by Arctic Adventures).
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ExcludeHandlersFromAssemblyTests
{
    private const string DummyConnectionString = "Host=x;Database=x;Username=x;Password=x";

    [TimedFact]
    public void Excludes_RequestHandler_FromGivenAssembly()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));
        services.AddWarp<TestContext>(opt =>
        {
            opt.UsePostgreSql();
            opt.ExcludeHandlersFromAssembly(typeof(EchoHandler).Assembly);
        });

        // EchoHandler lives in the Warp.Tests assembly (this test assembly). Excluding that
        // assembly removes its IRequestHandler<EchoRequest, EchoResponse> registration.
        var hasEcho = services.Any(d =>
            d.ServiceType == typeof(IRequestHandler<EchoRequest, EchoResponse>)
            && d.ImplementationType == typeof(EchoHandler));

        hasEcho.ShouldBeFalse();
    }

    [TimedFact]
    public void Default_KeepsHandlersFromCurrentAssembly()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));
        services.AddWarp<TestContext>(opt => opt.UsePostgreSql());

        var hasEcho = services.Any(d =>
            d.ServiceType == typeof(IRequestHandler<EchoRequest, EchoResponse>)
            && d.ImplementationType == typeof(EchoHandler));

        hasEcho.ShouldBeTrue("baseline: without ExcludeHandlersFromAssembly, EchoHandler is registered");
    }

    [TimedFact]
    public void Excludes_RequestHandler_WhenWiredViaAddWarpWorker()
    {
        // AddWarpWorker internally calls AddWarp, but registers IOptions<WarpConfiguration>
        // first via TryAddSingleton. The exclusion set on the WORKER builder must still take
        // effect — verifying that the post-hoc filter in CreateWarpServices reads from the
        // registered options instance, not from a fresh WarpBuilder created by AddWarp.
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));
        services.AddWarpWorker<TestContext>(opt =>
        {
            opt.UsePostgreSql();
            opt.ExcludeHandlersFromAssembly(typeof(EchoHandler).Assembly);
        });

        var hasEcho = services.Any(d =>
            d.ServiceType == typeof(IRequestHandler<EchoRequest, EchoResponse>)
            && d.ImplementationType == typeof(EchoHandler));

        hasEcho.ShouldBeFalse("exclusion set on AddWarpWorker builder is honored by the post-hoc filter");
    }
}

/// <summary>
/// Confirms <c>AddWarp&lt;T&gt;</c> fails fast at registration time when TContext isn't
/// registered as Scoped — most commonly because the host used AddDbContextFactory instead
/// of AddDbContext, or registered TContext with a non-Scoped lifetime. The colleague's
/// scenario in feedback §2.1 (silent empty migration) goes the other way (design-time
/// tooling), but at runtime we now refuse to start with a clear message instead of
/// failing later in handler resolution.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class AddWarpDbContextValidationTests
{
    [TimedFact]
    public void AddWarp_Throws_WhenDbContextNotRegistered()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(() => services.AddWarp<TestContext>());
        ex.Message.ShouldContain("AddDbContext");
        ex.Message.ShouldContain("TestContext");
    }

    [TimedFact]
    public void AddWarp_Throws_WhenDbContextRegisteredAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TestContext>(_ => throw new InvalidOperationException("should never resolve"));

        var ex = Should.Throw<InvalidOperationException>(() => services.AddWarp<TestContext>());
        ex.Message.ShouldContain("Scoped");
        ex.Message.ShouldContain("Singleton");
    }

    [TimedFact]
    public void AddWarpWorker_Throws_WhenDbContextNotRegistered()
    {
        // AddWarpWorker calls AddWarp internally, which is where the validation lives.
        // Confirm the error surfaces via the worker entry point too — multi-host solutions
        // are likely to call AddWarpWorker before AddDbContext if the registration order
        // is wrong in their composition root.
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(() => services.AddWarpWorker<TestContext>());
        ex.Message.ShouldContain("AddDbContext");
        ex.Message.ShouldContain("TestContext");
    }
}

/// <summary>
/// Pins the always-on addon-entity contract: AddWarp without any opt-ins must produce a
/// model that includes every addon entity. Guards against a future refactor accidentally
/// re-introducing conditional registration (which would silently re-create the multi-host
/// schema-mirroring footgun the always-on change was designed to eliminate).
/// </summary>
[Trait("Category", "NoDb")]
public sealed class AlwaysOnAddonEntitiesTests
{
    private const string DummyConnectionString = "Host=x;Database=x;Username=x;Password=x";

    [TimedFact]
    public void AddWarp_WithNoAddonOptIns_RegistersAllAddonEntities()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseNpgsql(DummyConnectionString));
        services.AddWarp<TestContext>(opt => opt.UsePostgreSql());

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();

        var entityTypes = ctx.Model.GetEntityTypes()
            .Select(e => e.ClrType)
            .ToHashSet();

        // Addon entities — registered unconditionally by WarpModelCustomizer.
        entityTypes.ShouldContain(typeof(ConcurrencyLimit), "ConcurrencyLimit must be in the model regardless of opt.AddConcurrency()");
        entityTypes.ShouldContain(typeof(CircuitBreakerState), "CircuitBreakerState must be in the model regardless of opt.AddCircuitBreaker()");
        entityTypes.ShouldContain(typeof(RateLimitBucket), "RateLimitBucket must be in the model regardless of opt.AddRateLimit()");
        entityTypes.ShouldContain(typeof(RateLimitOverride), "RateLimitOverride must be in the model regardless of opt.AddRateLimit()");
        entityTypes.ShouldContain(typeof(SagaState), "SagaState must be in the model regardless of opt.AddSagas()");
        entityTypes.ShouldContain(typeof(SagaJobLink), "SagaJobLink must be in the model regardless of opt.AddSagas()");

        // Schema is part of the always-on contract — entities must land in `warp` (the
        // default) so a `dotnet ef migrations` run produces the same DDL regardless of
        // which host runs it. A refactor that strips SetSchema or uses ToTable() would
        // silently break this without the schema assertion.
        var addonEntities = new[]
        {
            typeof(ConcurrencyLimit),
            typeof(CircuitBreakerState),
            typeof(RateLimitBucket),
            typeof(RateLimitOverride),
            typeof(SagaState),
            typeof(SagaJobLink),
        };
        foreach (var entity in addonEntities)
        {
            ctx.Model.FindEntityType(entity)!.GetSchema().ShouldBe("warp", $"{entity.Name} must use the default warp schema");
        }
    }
}

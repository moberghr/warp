using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;

namespace Warp.Tests.Core;

/// <summary>
/// Covers the explicit model-contribution path (<see cref="WarpModelExtensions.ApplyWarpModel"/>)
/// and the startup guard (<see cref="WarpModelGuard"/>) that replaces EF Core's cryptic
/// "Cannot create a DbSet for 'Job'" with an actionable message.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class WarpModelExtensionsTests
{
    [TimedFact]
    public void ApplyWarpModel_AddsJobEntityUnderSchema()
    {
        using var ctx = NewContext<AppliedContext>();

        var job = ctx.Model.FindEntityType(typeof(Job));
        job.ShouldNotBeNull();
        job.GetSchema().ShouldBe("warp");
    }

    [TimedFact]
    public void ApplyWarpModel_AddsAddonEntitiesUnconditionally()
    {
        using var ctx = NewContext<AppliedContext>();

        // §2.11 — addon entities are always in the schema regardless of opt-in.
        ctx.Model.GetEntityTypes()
            .Select(x => x.ClrType.Name)
            .ShouldContain("RateLimitBucket");
    }

    [TimedFact]
    public void ApplyWarpModel_IsIdempotent_WhenCalledTwice()
    {
        // Building the model must not throw on a second application — this is what lets the explicit
        // OnModelCreating call coexist with the DI customizer that AddWarp still wires.
        using var ctx = NewContext<DoubleAppliedContext>();

        ctx.Model.FindEntityType(typeof(Job)).ShouldNotBeNull();
    }

    [TimedFact]
    public void EnsureWarpModelApplied_Throws_WhenModelMissing()
    {
        using var ctx = NewContext<BareContext>();

        var ex = Should.Throw<InvalidOperationException>(() => WarpModelGuard.EnsureWarpModelApplied(ctx));

        ex.Message.ShouldContain("ApplyWarpModel");
        ex.Message.ShouldContain(nameof(BareContext));
    }

    [TimedFact]
    public void EnsureWarpModelApplied_Passes_WhenModelPresent()
    {
        using var ctx = NewContext<AppliedContext>();

        Should.NotThrow(() => WarpModelGuard.EnsureWarpModelApplied(ctx));
    }

    [TimedFact]
    public async Task ValidationService_Throws_WhenModelMissing()
    {
        await using var provider = BuildProvider<BareContext>();
        var service = new WarpModelValidationService<BareContext>(provider.GetRequiredService<IServiceScopeFactory>());

        await Should.ThrowAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
    }

    [TimedFact]
    public async Task ValidationService_Passes_WhenModelPresent()
    {
        await using var provider = BuildProvider<AppliedContext>();
        var service = new WarpModelValidationService<AppliedContext>(provider.GetRequiredService<IServiceScopeFactory>());

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
    }

    private static TContext NewContext<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseInMemoryDatabase($"warp-model-{Guid.NewGuid():N}")
            .Options;

        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    private static ServiceProvider BuildProvider<TContext>()
        where TContext : DbContext
    {
        var services = new ServiceCollection();
        services.AddDbContext<TContext>(x => x.UseInMemoryDatabase($"warp-val-{Guid.NewGuid():N}"));

        return services.BuildServiceProvider();
    }

    private sealed class BareContext : DbContext
    {
        public BareContext(DbContextOptions<BareContext> options)
            : base(options)
        {
        }
    }

    private sealed class AppliedContext : DbContext
    {
        public AppliedContext(DbContextOptions<AppliedContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyWarpModel();
        }
    }

    private sealed class DoubleAppliedContext : DbContext
    {
        public DoubleAppliedContext(DbContextOptions<DoubleAppliedContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyWarpModel();
            modelBuilder.ApplyWarpModel();
        }
    }
}

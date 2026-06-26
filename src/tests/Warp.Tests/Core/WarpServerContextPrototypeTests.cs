using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;

namespace Warp.Tests.Core;

/// <summary>
/// PROTOTYPE de-risking for the server-context design (spec 2026-06-25). Proves the bootstrap:
/// a Warp-owned <c>WarpServerContext&lt;TContext&gt;</c> resolves the user's TContext model at its
/// own build time and mirrors the resolved physical names — so it maps to the identical tables even
/// when the user applied a naming convention (snake_case), without replaying the convention.
/// Model-build only (Npgsql, no connection / no container), so it stays NoDb.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class WarpServerContextPrototypeTests
{
    private const string DummyConnection = "Host=localhost;Database=warp_proto";

    [TimedFact]
    public void ServerContext_MirrorsTableAndSchema_FromSnakeCaseUserContext()
    {
        using var provider = BuildProvider();

        var userJob = UserEntity(provider);
        var serverJob = ServerEntity(provider);

        serverJob.GetTableName().ShouldBe(userJob.GetTableName());
        serverJob.GetSchema().ShouldBe(userJob.GetSchema());
    }

    [TimedFact]
    public void ServerContext_MirrorsColumnNames_FromSnakeCaseUserContext()
    {
        using var provider = BuildProvider();

        var userJob = UserEntity(provider);
        var serverJob = ServerEntity(provider);

        var userColumn = ColumnName(userJob, nameof(Job.CurrentState));
        var serverColumn = ColumnName(serverJob, nameof(Job.CurrentState));

        // Sanity check that the snake_case convention actually transformed the name on the user
        // context, then assert the server context mirrored it without applying the convention itself.
        userColumn.ShouldBe("current_state");
        serverColumn.ShouldBe(userColumn);
    }

    [TimedFact]
    public void ServerContext_ExcludesWarpTablesFromMigrations()
    {
        using var provider = BuildProvider();

        // ExcludeFromMigrations is a design-time-model concern, not part of the read-optimized
        // runtime model — assert against the design-time model.
        var designModel = provider.GetRequiredService<WarpServerContext<SnakeContext>>()
            .GetService<IDesignTimeModel>()
            .Model;

        designModel.FindEntityType(typeof(Job))!.IsTableExcludedFromMigrations().ShouldBeTrue();
    }

    private static IReadOnlyEntityType UserEntity(ServiceProvider provider)
    {
        return provider.GetRequiredService<SnakeContext>().Model.FindEntityType(typeof(Job))!;
    }

    private static IReadOnlyEntityType ServerEntity(ServiceProvider provider)
    {
        return provider.GetRequiredService<WarpServerContext<SnakeContext>>().Model.FindEntityType(typeof(Job))!;
    }

    private static string? ColumnName(IReadOnlyEntityType entity, string propertyName)
    {
        var store = StoreObjectIdentifier.Create(entity, StoreObjectType.Table)!.Value;

        return entity.FindProperty(propertyName)!.GetColumnName(store);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SnakeContext>(x => x
            .UseNpgsql(DummyConnection)
            .UseSnakeCaseNamingConvention());
        services.AddDbContext<WarpServerContext<SnakeContext>>(x => x
            .UseNpgsql(DummyConnection));
        services.AddSingleton<IOptions<WarpConfiguration>>(Options.Create(new WarpConfiguration()));
        services.AddSingleton<IWarpServerModelNames>(sp =>
            new WarpServerModelNames<SnakeContext>(sp.GetRequiredService<IServiceScopeFactory>()));

        return services.BuildServiceProvider();
    }

    private sealed class SnakeContext : DbContext
    {
        public SnakeContext(DbContextOptions<SnakeContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyWarpModel();
        }
    }
}

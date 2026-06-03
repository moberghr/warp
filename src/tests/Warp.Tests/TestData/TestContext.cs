using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.Data.Converters;

namespace Warp.Tests;

public class TestContext : DbContext
{
    private readonly string? _schema;

    public TestContext(DbContextOptions<TestContext> options, string? schema = "warp")
        : base(options)
    {
        _schema = schema;
    }

    public DbSet<TestLog> TestLogs => Set<TestLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddOutboxStateEntity(_schema);

        // Tests construct TestContext directly via fixtures, bypassing the
        // WarpModelCustomizer that runs in real DI hosts. Mirror what the customizer
        // adds unconditionally — all addon entities — so fixture-built contexts have the
        // same schema as production.
        ServiceConfiguration.AddCircuitBreakerStateEntity(modelBuilder, _schema);
        ServiceConfiguration.AddConcurrencyLimitEntity(modelBuilder, _schema);
        ServiceConfiguration.AddRateLimitBucketEntity(modelBuilder, _schema);
        ServiceConfiguration.AddRateLimitOverrideEntity(modelBuilder, _schema);
        ServiceConfiguration.AddSagaStateEntity(modelBuilder, _schema);
        ServiceConfiguration.AddSagaJobLinkEntity(modelBuilder, _schema);

        // Mirror what WarpModelCustomizer does in production. The unit-fixture path bypasses
        // ReplaceService<IModelCustomizer> by building DbContextOptions directly, so we apply
        // the convention here to keep fixture-backed tests aligned with real DI behavior.
        modelBuilder.ApplyWarpUtcDateTimeConverters();
    }
}

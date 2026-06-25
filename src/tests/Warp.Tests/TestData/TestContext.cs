using Microsoft.EntityFrameworkCore;
using Warp.Core;

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

        // Tests construct TestContext directly via fixtures, bypassing the WarpModelCustomizer
        // that runs in real DI hosts. ApplyWarpModel is the same public model contribution the
        // customizer routes through, so fixture-built contexts get an identical schema +
        // converters. This is also the sanctioned design-time pattern consumers use.
        modelBuilder.ApplyWarpModel(_schema);
    }
}

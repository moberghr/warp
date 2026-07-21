using Microsoft.EntityFrameworkCore;
using Warp.Test.Shared.Entities;

namespace Warp.Core;

public class TestContext : DbContext
{
    public TestContext(DbContextOptions<TestContext> options)
        : base(options)
    {
    }

    public DbSet<Registration> Registrations => Set<Registration>();

    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ShopOrder> Orders => Set<ShopOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>().HasKey(x => x.Sku);
    }
}

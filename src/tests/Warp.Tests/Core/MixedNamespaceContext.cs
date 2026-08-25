using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.Data.Converters;

namespace Warp.Tests.Core;

internal sealed class MixedNamespaceContext : DbContext
{
    public MixedNamespaceContext(DbContextOptions<MixedNamespaceContext> options)
        : base(options)
    {
    }

    public DbSet<Acme.Domain.UserOrder> UserOrders => Set<Acme.Domain.UserOrder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.AddOutboxStateEntity("warp");
        modelBuilder.PinWarpStorageTypes();
    }
}

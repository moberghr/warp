using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

// Applies the resolved physical names (from IWarpServerModelNames) onto the server context's model
// so it maps to the same tables as TContext without replaying any naming convention, and excludes
// the tables from the server context's migrations (TContext stays the schema owner).
internal static class WarpServerModel
{
    public static void MirrorNames(ModelBuilder modelBuilder, IWarpServerModelNames names)
    {
        foreach (var serverEntity in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var entityNames = names.GetNames(serverEntity.ClrType);
            if (entityNames is null)
            {
                continue;
            }

            var entityBuilder = modelBuilder.Entity(serverEntity.ClrType);
            entityBuilder.ToTable(entityNames.Table, entityNames.Schema, x => x.ExcludeFromMigrations());

            var columns = serverEntity.GetProperties()
                .Select(x =>
                    new
                    {
                        x.Name,
                        Column = entityNames.Columns.GetValueOrDefault(x.Name),
                    })
                .Where(x => x.Column is not null);

            foreach (var column in columns)
            {
                entityBuilder.Property(column.Name).HasColumnName(column.Column!);
            }
        }
    }
}

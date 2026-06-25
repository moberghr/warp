using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Warp.Core;

// PROTOTYPE (spec 2026-06-25-warp-server-context). Mirrors the physical table/schema/column names
// that the user's TContext resolved (post-naming-convention) onto the server context's model, so the
// server context maps to the identical tables without replaying the convention. Reads the same
// resolved metadata WarpJobTableNames uses for raw provider SQL. Pins names explicitly + excludes
// the tables from the server context's migrations (TContext stays the schema owner).
internal static class WarpServerModel
{
    public static void MirrorNames(ModelBuilder modelBuilder, IModel sourceModel)
    {
        foreach (var serverEntity in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var sourceEntity = sourceModel.FindEntityType(serverEntity.ClrType);
            if (sourceEntity is null)
            {
                continue;
            }

            var tableName = sourceEntity.GetTableName();
            var sourceStore = StoreObjectIdentifier.Create(sourceEntity, StoreObjectType.Table);
            if (tableName is null || sourceStore is null)
            {
                continue;
            }

            var entityBuilder = modelBuilder.Entity(serverEntity.ClrType);
            entityBuilder.ToTable(tableName, sourceEntity.GetSchema(), x => x.ExcludeFromMigrations());

            var columns = serverEntity.GetProperties()
                .Select(x =>
                    new
                    {
                        x.Name,
                        Column = sourceEntity.FindProperty(x.Name)?.GetColumnName(sourceStore.Value),
                    })
                .Where(x => !string.IsNullOrEmpty(x.Column));

            foreach (var column in columns)
            {
                entityBuilder.Property(column.Name).HasColumnName(column.Column!);
            }
        }
    }
}

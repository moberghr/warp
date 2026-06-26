using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Core;

// Default IWarpServerModelNames: reads the resolved table/schema/column names from the user's
// TContext model, once, and caches them. Resolving TContext needs a scope (it's scoped, §5.5), so
// this is the single place that does it — keeping WarpServerContext and the server tasks free of any
// TContext dependency. Swap this registration to source the names elsewhere in future.
internal sealed class WarpServerModelNames<TContext> : IWarpServerModelNames
    where TContext : DbContext
{
    private readonly Lazy<Dictionary<Type, WarpEntityNames>> _names;

    public WarpServerModelNames(IServiceScopeFactory scopeFactory)
    {
        _names = new Lazy<Dictionary<Type, WarpEntityNames>>(() => Build(scopeFactory));
    }

    public WarpEntityNames? GetNames(Type entityClrType)
    {
        return _names.Value.GetValueOrDefault(entityClrType);
    }

    private static Dictionary<Type, WarpEntityNames> Build(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<TContext>().Model;

        var names = new Dictionary<Type, WarpEntityNames>();
        foreach (var entity in model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            var store = StoreObjectIdentifier.Create(entity, StoreObjectType.Table);
            if (table is null || store is null)
            {
                continue;
            }

            var columns = entity.GetProperties()
                .Select(x =>
                    new
                    {
                        x.Name,
                        Column = x.GetColumnName(store.Value),
                    })
                .Where(x => !string.IsNullOrEmpty(x.Column))
                .ToDictionary(x => x.Name, x => x.Column!);

            names[entity.ClrType] = new WarpEntityNames(table, entity.GetSchema(), columns);
        }

        return names;
    }
}

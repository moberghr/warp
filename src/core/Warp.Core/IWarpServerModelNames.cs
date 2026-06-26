namespace Warp.Core;

/// <summary>
/// Supplies the resolved physical names (table, schema, per-property columns) that Warp's runtime
/// mirror context (<c>WarpServerContext</c>) maps its entities to. Abstracted from the source:
/// today the names are read from the user's <c>TContext</c> model so a naming convention is honoured
/// (see the default implementation), but they could come from elsewhere — a generated snapshot,
/// configuration — without the server context knowing or depending on <c>TContext</c>.
/// </summary>
public interface IWarpServerModelNames
{
    /// <summary>The resolved names for <paramref name="entityClrType"/>, or <c>null</c> if it isn't mapped.</summary>
    WarpEntityNames? GetNames(Type entityClrType);
}

/// <summary>Resolved physical names for one entity: its table, schema, and column-name-by-property map.</summary>
public sealed record WarpEntityNames(string Table, string? Schema, IReadOnlyDictionary<string, string> Columns);

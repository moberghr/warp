using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Entities;

namespace Warp.Core.Data;

/// <summary>One of the job table's partial (filtered) indexes as a model names it (rule 6.15).</summary>
/// <param name="Name">The index's name in the database.</param>
/// <param name="Schema">The job table's schema, or null for the database's default.</param>
/// <param name="Table">The job table's name.</param>
/// <param name="Columns">The indexed columns, in order, as the model names them.</param>
/// <param name="Filter">The index filter, SQL naming the columns as the model names them.</param>
/// <param name="IsUnique">Whether the index is unique.</param>
public sealed record WarpJobIndex(string Name, string? Schema, string Table, IReadOnlyList<string> Columns, string Filter, bool IsUnique);

/// <summary>
/// The job table's partial indexes (rule 6.15), read from a model: the application's, or a migration's
/// <c>TargetModel</c>, which is how the provider packages' online index helpers learn the names, columns and
/// filters of the host's schema and naming convention without a hard-coded string.
/// </summary>
public static class WarpJobIndexes
{
    // The property lists of the job table's partial indexes; WarpPartialIndexes sets their filters.
    internal static readonly string[][] PartialIndexProperties =
    [
        [nameof(Job.Kind), nameof(Job.CurrentState), nameof(Job.Queue), nameof(Job.ScheduleTime)],
        [nameof(Job.CurrentState), nameof(Job.ScheduleTime)],
        [nameof(Job.ParentJobId), nameof(Job.CurrentState)],
        [nameof(Job.ExpireAt)],
    ];

    /// <summary>
    /// The job table's partial indexes in <paramref name="model"/> that carry a filter. Looked up by entity
    /// NAME, so a migration's target model (built from its snapshot, where entity types are named rather
    /// than bound to CLR types) works as well as the runtime model. An index a host removed is left out.
    /// </summary>
    public static IReadOnlyList<WarpJobIndex> FindPartial(IReadOnlyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var job = model.FindEntityType(typeof(Job).FullName!)
            ?? throw new InvalidOperationException($"The model has no {typeof(Job).FullName} entity: is ApplyWarpModel (AddWarp) part of it?");
        var table = job.GetTableName()
            ?? throw new InvalidOperationException($"{typeof(Job).FullName} has no table name in the model.");
        var schema = job.GetSchema();

        return
        [
            .. PartialIndexProperties
            .Select(x => job.GetIndexes().SingleOrDefault(y => y.Properties.Select(z => z.Name).SequenceEqual(x, StringComparer.Ordinal)))
            .OfType<IReadOnlyIndex>()
            .Where(x => !string.IsNullOrEmpty(x.GetFilter()))
            .Select(x =>
                new WarpJobIndex(
                    x.GetDatabaseName() ?? throw new InvalidOperationException("A job table index has no name in the model."),
                    schema,
                    table,
                    [.. x.Properties.Select(y => y.GetColumnName())],
                    x.GetFilter()!,
                    x.IsUnique)),
        ];
    }
}

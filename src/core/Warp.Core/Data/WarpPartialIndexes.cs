using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Core.Data;

/// <summary>
/// Narrows the job table's indexes to the rows their readers look for (rule 6.15).
/// <para>
/// An update that changes an indexed column (every state transition does) writes a new entry into
/// EVERY index of the table, unless the index is partial and the new row falls outside it. A job is
/// inserted, claimed and finished: with whole indexes that was seven entries each time, twenty-one per
/// job, for indexes most of whose entries nobody reads. Narrowed, the claim index holds only Enqueued
/// rows, scheduled activation's only Scheduled rows, and the parent and expiry indexes only rows that
/// have a value. Measured on the load lab (10,000 jobs, dispatcher mode, PostgreSQL): WAL 3,777 to
/// 2,932 bytes per job, 78 to 58 buffer hits per job.
/// </para>
/// <para>
/// The filters are SQL, so they name the columns as the host's naming conventions left them (applied
/// as each property is declared, so known here). They quote them with double quotes, which both
/// databases accept: PostgreSQL always, SQL Server with QUOTED_IDENTIFIER on, which a filtered index
/// requires anyway. The readers state the same predicates as literals (the claim and scheduled
/// activation, rule 6.9) or imply them (<c>= @parent</c> implies <c>IS NOT NULL</c>), which is what lets
/// both databases use a partial index.
/// </para>
/// </summary>
internal static class WarpPartialIndexes
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        var job = modelBuilder.Model.FindEntityType(typeof(Job));
        if (job == null)
        {
            return;
        }

        string Column(string property) => $"\"{job.FindProperty(property)!.GetColumnName()}\"";

        var state = Column(nameof(Job.CurrentState));
        SetFilter(job, [nameof(Job.Kind), nameof(Job.CurrentState), nameof(Job.Queue), nameof(Job.ScheduleTime)], $"{state} = {(int)State.Enqueued}");
        SetFilter(job, [nameof(Job.CurrentState), nameof(Job.ScheduleTime)], $"{state} = {(int)State.Scheduled}");
        SetFilter(job, [nameof(Job.ParentJobId), nameof(Job.CurrentState)], $"{Column(nameof(Job.ParentJobId))} IS NOT NULL");
        SetFilter(job, [nameof(Job.ExpireAt)], $"{Column(nameof(Job.ExpireAt))} IS NOT NULL");
    }

    private static void SetFilter(IMutableEntityType job, string[] properties, string filter)
    {
        var index = job.GetIndexes()
            .SingleOrDefault(x => x.Properties.Select(y => y.Name).SequenceEqual(properties, StringComparer.Ordinal));

        // A host that removed one of Warp's indexes keeps its choice.
        index?.SetFilter(filter);
    }
}

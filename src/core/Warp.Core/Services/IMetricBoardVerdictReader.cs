using Warp.Core.Models;

namespace Warp.Core.Services;

// The verdict's read of every board when it is given no board. That verdict reads EVERY objective of the application
// itself (one GetObjectives), so the SLO tiles' join to a second objective read is skipped: their Slo is null and they
// are not flagged missing. Internal on purpose: the public IMetricBoardQueryService keeps its behaviour, and a host's own
// implementation of it (TryAdd) simply goes through GetAllBoards.
internal interface IMetricBoardVerdictReader
{
    Task<MetricBoardModel> GetAllBoardsWithoutObjectives(DateTime rangeStart, DateTime rangeEnd, CancellationToken ct);
}

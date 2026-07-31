using Warp.Core.Enums;
using Warp.Core.Models;

namespace Warp.Core.Services;

/// <summary>
/// Read side of the error grouping / Issues feature (§8.29), registered by <c>AddWarp</c> itself so dashboard-only /
/// publisher-only processes resolve it. The list + detail read the durable <c>ErrorGroup</c> rows; the detail's
/// trend is folded from the durable <c>errorgroup:</c> Counter/Statistic keys (survives raw-row cleanup, §8.22).
/// </summary>
public interface IErrorGroupQueryService
{
    Task<ErrorGroupListModel> GetGroups(ErrorSource? source, ErrorGroupStatus? status, string? application, ErrorKind? kind, int page, int pageSize, CancellationToken ct);

    Task<ErrorGroupDetailModel?> GetGroup(string fingerprint, CancellationToken ct);
}

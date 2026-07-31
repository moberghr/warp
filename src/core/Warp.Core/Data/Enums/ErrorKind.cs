namespace Warp.Core.Enums;

/// <summary>
/// Whether an <see cref="Warp.Core.Data.Entities.ErrorGroup"/> was grouped from a thrown exception or from a
/// non-exception status-code signal (§8.29). Endpoint 4xx have no exception/stack, so they group by
/// <c>status + route</c> instead — a distinct kind, default-filtered in the UI and kept off the reliability SLI.
/// Values from 1 (§8.11).
/// </summary>
public enum ErrorKind
{
    /// <summary>A thrown exception grouped by type + top in-app stack frame (or culprit).</summary>
    Exception = 1,

    /// <summary>A non-exception status-code signal (endpoint 4xx) grouped by status + route.</summary>
    StatusCode = 2,
}

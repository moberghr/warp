namespace Warp.Core.Enums;

/// <summary>
/// The operator-controlled lifecycle state of an <see cref="Warp.Core.Data.Entities.ErrorGroup"/> (§8.29).
/// <see cref="Resolved"/> re-opens to <see cref="Unresolved"/> on a later occurrence (regression);
/// <see cref="Ignored"/> is a deliberate mute that still counts but never auto-re-opens. Values from 1 (§8.11).
/// </summary>
public enum ErrorGroupStatus
{
    Unresolved = 1,

    Resolved = 2,

    Ignored = 3,
}

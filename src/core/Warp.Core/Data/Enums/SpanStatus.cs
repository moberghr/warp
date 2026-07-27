namespace Warp.Core.Enums;

/// <summary>Terminal status of a span (§8.28), mirroring OTel StatusCode. Values from 1 (§8.11).</summary>
public enum SpanStatus
{
    Unset = 1,
    Ok = 2,
    Error = 3,
}

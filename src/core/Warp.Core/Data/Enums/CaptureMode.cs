namespace Warp.Core.Enums;

/// <summary>
/// Per-adapter capture tier for request/response bodies and headers. <see cref="None"/> stores
/// nothing; <see cref="OnFailure"/> stores payloads only on non-success outcomes;
/// <see cref="Always"/> stores them on success too. Captured values are always truncated and
/// redacted before storage (§1.2).
/// </summary>
public enum CaptureMode
{
    None = 1,
    OnFailure = 2,
    Always = 3,
}

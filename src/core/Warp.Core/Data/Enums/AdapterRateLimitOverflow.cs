namespace Warp.Core.Enums;

/// <summary>
/// Behaviour when a shared-rate-limited adapter cannot acquire a token within its budget.
/// <see cref="Wait"/> delays (up to a bounded max) for the next window/lease; <see cref="FailFast"/>
/// throws <c>AdapterRateLimitedException</c> immediately. Both surface as a <c>Throttled</c> outcome.
/// </summary>
public enum AdapterRateLimitOverflow
{
    Wait = 1,
    FailFast = 2,
}

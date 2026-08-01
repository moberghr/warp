namespace Warp.Core.ClientObservability;

/// <summary>
/// Presence marker registered by <c>AddClientObservability</c> regardless of sink (§8.24), so the dashboard
/// nav flag (<c>WarpAddonsInfo.Client</c>) lights up whether recording goes to the DB or OTel — mirrors
/// <c>IEndpointObservabilityMarker</c>.
/// </summary>
public interface IClientObservabilityMarker;

public sealed class ClientObservabilityMarker : IClientObservabilityMarker;

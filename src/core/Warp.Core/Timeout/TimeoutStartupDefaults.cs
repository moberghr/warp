namespace Warp.Core.Timeout;

/// <summary>
/// Snapshot of the global timeout default taken by <c>AddTimeout</c> at registration time, so
/// <c>ValidateAddonAttributesOnHandlers</c> can reject a handler-declared <c>[Timeout]</c> under a
/// <see cref="TimeoutScope.Total"/>-scoped default (which keeps publish-stamping and would shadow the
/// handler attribute forever) without building a service provider. A host that additionally configures
/// <see cref="TimeoutOptions"/> outside <c>AddTimeout</c> escapes this snapshot — the same
/// registered-state-only caveat the rest of the startup validation carries.
/// </summary>
internal sealed record TimeoutStartupDefaults(bool HasDefault, TimeoutScope DefaultScope);

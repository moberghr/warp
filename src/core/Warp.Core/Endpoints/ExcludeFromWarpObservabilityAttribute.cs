namespace Warp.Core.Endpoints;

/// <summary>
/// Endpoint-metadata marker that keeps an endpoint out of inbound endpoint observability even when it sits
/// under an opted-in convention (<c>MapControllers().WithWarpObservability()</c>, a group, or a
/// <c>[WarpObserved]</c> controller). Use it on a controller or action (e.g. a health or polling action) or
/// attach it with <c>.WithMetadata(new ExcludeFromWarpObservabilityAttribute())</c>. The Warp dashboard stamps it
/// on every endpoint it maps, so the dashboard is never observed. It applies to the host's opted-in
/// endpoints; Warp endpoints mapped by <c>MapWarpHttp</c> are always observed. Lives in Core (not
/// <c>Warp.Http</c>) so the dashboard can stamp it without depending on <c>Warp.Http</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class ExcludeFromWarpObservabilityAttribute : Attribute;

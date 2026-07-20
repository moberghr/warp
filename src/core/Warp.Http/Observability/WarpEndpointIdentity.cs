namespace Warp.Http.Observability;

/// <summary>
/// Endpoint-metadata marker attached to every Warp-mapped HTTP endpoint by <c>MapWarpHttp</c>. The inbound
/// observability middleware reads it off <c>HttpContext.GetEndpoint().Metadata</c> to (a) recognise a Warp
/// endpoint (it no-ops for anything without this marker, so it never observes the host's own controllers or
/// the dashboard) and (b) record a stable identity: the HTTP <see cref="Method"/> + route
/// <see cref="RouteTemplate"/> (the bounded identity) and the handler/route <see cref="Operation"/> name.
/// </summary>
public sealed record WarpEndpointIdentity(string Method, string RouteTemplate, string Operation);

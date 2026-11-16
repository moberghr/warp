namespace Warp.Http.Observability;

/// <summary>
/// Endpoint-metadata marker attached to every Warp-mapped HTTP endpoint by <c>MapWarpHttp</c>, and to every
/// host endpoint opted in through <c>WithWarpObservability()</c>. The inbound observability middleware reads it
/// off <c>HttpContext.GetEndpoint().Metadata</c> to (a) recognise an observed endpoint and (b) record a stable
/// identity: the HTTP <see cref="Method"/> + route
/// <see cref="RouteTemplate"/> (the bounded identity) and the handler/route <see cref="Operation"/> name.
/// </summary>
public sealed record WarpEndpointIdentity(string Method, string RouteTemplate, string Operation);

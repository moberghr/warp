namespace Warp.Http.Observability;

/// <summary>
/// Opts a host endpoint into inbound endpoint observability. Put it on an MVC controller (every action) or
/// on a single action; <c>.WithWarpObservability()</c> attaches the same marker through an endpoint
/// convention. An opted-in endpoint is recorded exactly like a Warp endpoint — same middleware, capture tiers,
/// redaction, sampling, sinks, meters and Issues grouping. The identity (method + route template + operation)
/// is built from the matched route endpoint, never the raw request path, so cardinality stays bounded.
/// <see cref="Warp.Core.Endpoints.ExcludeFromWarpObservabilityAttribute"/> on the endpoint wins over this marker.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class WarpObservedAttribute : Attribute
{
    /// <summary>
    /// Optional operation name recorded on every call. When null, the operation is the endpoint name
    /// (<c>WithName</c> / route name), else <c>Controller.Action</c> for an MVC action, else the endpoint's
    /// display name.
    /// </summary>
    public string? Operation { get; set; }
}

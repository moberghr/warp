namespace Warp.Adapters.Http;

/// <summary>
/// Per-request Warp adapter hints carried on <see cref="HttpRequestMessage.Options"/>. These take
/// precedence over the ambient <see cref="WarpAdapterCall"/> scopes and the URL heuristic, and are the
/// reliable naming path when the request object is in hand (Refit, hand-rolled SOAP, webhook fan-out).
/// </summary>
public static class HttpRequestMessageExtensions
{
    internal static readonly HttpRequestOptionsKey<string> OperationKey = new("warp.adapter.operation");
    internal static readonly HttpRequestOptionsKey<string> GroupKey = new("warp.adapter.group");
    internal static readonly HttpRequestOptionsKey<string> CorrelationKey = new("warp.adapter.correlation");

    /// <summary>
    /// Names the operation for this request (<c>GetOrders</c>, <c>payment.capture</c>). Wins over the
    /// ambient scope and the URL heuristic; never subject to the operation cardinality guard.
    /// </summary>
    public static HttpRequestMessage WithWarpOperation(this HttpRequestMessage request, string operation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        request.Options.Set(OperationKey, operation);

        return request;
    }

    /// <summary>
    /// Sets the group (runtime who/where — destination endpoint, tenant, shop) for this request. Wins
    /// over the ambient <see cref="WarpAdapterCall.Group"/> scope. Groups are always explicit — there is
    /// no URL heuristic tier.
    /// </summary>
    public static HttpRequestMessage WithWarpGroup(this HttpRequestMessage request, string group)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        request.Options.Set(GroupKey, group);

        return request;
    }

    /// <summary>
    /// Links this call to a caller-owned domain record (e.g. a webhook delivery id) via the generic,
    /// feature-agnostic correlation id recorded on the call-log row.
    /// </summary>
    public static HttpRequestMessage WithWarpCorrelation(this HttpRequestMessage request, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        request.Options.Set(CorrelationKey, correlationId);

        return request;
    }

    internal static string? GetWarpOperation(this HttpRequestMessage request)
        => request.Options.TryGetValue(OperationKey, out var value) ? value : null;

    internal static string? GetWarpGroup(this HttpRequestMessage request)
        => request.Options.TryGetValue(GroupKey, out var value) ? value : null;

    internal static string? GetWarpCorrelation(this HttpRequestMessage request)
        => request.Options.TryGetValue(CorrelationKey, out var value) ? value : null;
}

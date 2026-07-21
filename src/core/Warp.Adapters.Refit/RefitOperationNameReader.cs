using Refit;
using Warp.Adapters.Http;

namespace Warp.Adapters.Refit;

/// <summary>
/// Outermost <see cref="DelegatingHandler"/> for a Refit-registered adapter. Refit stamps a
/// <see cref="RestMethodInfo"/> onto <see cref="HttpRequestMessage.Options"/> when it builds each
/// request; this handler reads it and names the operation after the interface method
/// (<c>GetOrders</c>, <c>CreatePayment</c>) by pushing an ambient <see cref="WarpAdapterCall.Operation"/>
/// scope around the send. Because it runs before <c>WarpAdapterHandler</c> resolves the name, the method
/// name wins over the URL heuristic (and is never subject to the operation cardinality guard), while an
/// explicit per-request <see cref="HttpRequestMessageExtensions.WithWarpOperation"/> still takes
/// precedence. Non-Refit requests (no <see cref="RestMethodInfo"/> present) pass through untouched.
/// <para>
/// This is the <b>only</b> Warp component that references Refit — the operation name is bridged to
/// <c>Warp.Adapters.Http</c> exclusively through its public ambient-scope API, so no Refit type leaks
/// across the package boundary.
/// </para>
/// </summary>
internal sealed class RefitOperationNameReader : DelegatingHandler
{
    private static readonly HttpRequestOptionsKey<RestMethodInfo> RestMethodInfoKey = new(HttpRequestMessageOptions.RestMethodInfo);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var operation = ReadOperationName(request);
        if (operation is null)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        using (WarpAdapterCall.Operation(operation))
        {
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the Refit interface method name from the request's <see cref="RestMethodInfo"/> option, or
    /// <see langword="null"/> when the request was not built by Refit (or carries no method name).
    /// </summary>
    internal static string? ReadOperationName(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Options.TryGetValue(RestMethodInfoKey, out var info) && !string.IsNullOrWhiteSpace(info?.Name))
        {
            return info.Name;
        }

        return null;
    }
}

using Microsoft.AspNetCore.Builder;

namespace Warp.Http.Observability;

/// <summary>
/// Opts the host's own endpoints into inbound endpoint observability. There is no global switch: only the
/// endpoints this convention (or <see cref="WarpObservedAttribute"/>) covers are observed, and each is then
/// recorded exactly like a Warp endpoint.
/// </summary>
public static class WarpObservabilityEndpointConventionBuilderExtensions
{
    /// <summary>
    /// Observes every endpoint this builder covers — <c>app.MapControllers().WithWarpObservability()</c>,
    /// <c>app.MapGet(...).WithWarpObservability()</c> or <c>group.WithWarpObservability()</c>. Each endpoint gets
    /// its own <see cref="WarpEndpointIdentity"/>: the HTTP method (<c>*</c> when it answers several or any), the
    /// route template (never the raw request path) and the operation — <paramref name="operation"/> when given,
    /// else the endpoint/route name, else <c>Controller.Action</c> for an MVC action, else the display name. An
    /// endpoint that already has an identity (a <c>MapWarpHttp</c> endpoint) keeps it, and one carrying
    /// <c>ExcludeFromWarpObservabilityAttribute</c> (every dashboard endpoint does) is never observed. Requires
    /// <c>AddEndpointObservability()</c> and <c>UseWarpHttpObservability()</c>.
    /// </summary>
    public static TBuilder WithWarpObservability<TBuilder>(this TBuilder builder, string? operation = null)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var marker = new WarpObservedAttribute { Operation = string.IsNullOrWhiteSpace(operation) ? null : operation };

        // The marker alone is enough for the middleware to observe the endpoint; Finally then builds the identity
        // once at startup from the FINAL endpoint (every other convention applied — so it can see an identity a
        // Warp endpoint adds, or an exclusion marker) instead of per request.
        builder.Add(x => x.Metadata.Add(marker));
        builder.Finally(x =>
        {
            if (WarpEndpointIdentityFactory.TryCreate(x) is { } identity)
            {
                x.Metadata.Add(identity);
            }
        });

        return builder;
    }
}

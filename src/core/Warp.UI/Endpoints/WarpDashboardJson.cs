using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Warp.UI.Endpoints;

/// <summary>
/// JSON serialization owned by the dashboard API, deliberately independent of the host's
/// <c>ConfigureHttpJsonOptions</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dashboard endpoints are minimal APIs returning POCOs, so by default they serialize with the
/// host application's <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c> — process-wide options the
/// host configures for its OWN API. A host that registers a <c>JsonStringEnumConverter</c> (a common
/// choice) therefore silently reshapes Warp's payloads: <c>currentState</c> arrives as
/// <c>"Failed"</c> instead of <c>5</c>, and the bundled dashboard — which looks states up
/// numerically — renders the badge as "Unknown", loses the Requeue/Delete actions (they test
/// <c>kind === 1</c>), and can't show the "Cancelling…" state. A <c>PropertyNamingPolicy</c> change
/// breaks it the same way.
/// </para>
/// <para>
/// The dashboard API and the dashboard bundle ship together as one closed contract, so the wire
/// format is Warp's to pin, not the host's to configure. <see cref="Options"/> is
/// <see cref="JsonSerializerDefaults.Web"/>: camelCase names, numeric enums — exactly what the
/// TypeScript client decodes.
/// </para>
/// <para>
/// Only the RESPONSE direction needs pinning. Request bodies bind before any endpoint filter runs,
/// but <c>JsonStringEnumConverter</c> reads numbers as well as names, so the dashboard's numeric
/// enum posts keep deserializing under a host converter.
/// </para>
/// </remarks>
internal static class WarpDashboardJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Re-emits a route handler's return value through <see cref="Options"/>. Registered as a group
    /// filter on <c>{RoutePrefix}/api</c>, so it covers every dashboard route (including extension
    /// sub-groups) without each endpoint opting in.
    /// </summary>
    /// <remarks>
    /// Value-carrying results (<c>Results.Ok(x)</c>, <c>Results.Conflict(x)</c>, a bare POCO return)
    /// are re-serialized under the original status code. Everything else — <c>NotFound</c>,
    /// <c>NoContent</c>, <c>Unauthorized</c>, a bare <c>Results.Ok()</c>, files, redirects — has no
    /// body to reshape and passes through untouched. An extension endpoint that hands back its own
    /// <c>Results.Json(value, itsOwnOptions)</c> is re-serialized with Warp's options; uniformity
    /// across the dashboard API is the intent here.
    /// </remarks>
    internal static async ValueTask<object?> NormalizeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        if (result is IValueHttpResult { Value: not null } valueResult)
        {
            return Results.Json(
                valueResult.Value,
                Options,
                statusCode: (result as IStatusCodeHttpResult)?.StatusCode);
        }

        if (result is null or IResult)
        {
            return result;
        }

        return Results.Json(result, Options);
    }
}

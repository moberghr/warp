using Microsoft.AspNetCore.Http;
using Warp.Core.Metrics;

namespace Warp.Dashboard.Endpoints;

// The acting dashboard user for an audited write (#333): the authenticated identity's name, whichever gate set it — the
// host's own scheme behind RequireAuthorization or the built-in cookie login (ClaimTypes.Name) — and "anonymous" on an
// open dashboard (§1.5) or for an identity without a name. Never read from the request body.
internal static class DashboardUser
{
    public static string Name(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identity = context.User.Identity;

        return MetricAnnotationValidation.NormalizeAuthor(identity?.IsAuthenticated == true ? identity.Name : null);
    }
}

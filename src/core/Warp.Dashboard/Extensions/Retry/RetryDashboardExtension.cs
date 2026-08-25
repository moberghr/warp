using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace Warp.Dashboard.Extensions.Retry;

/// <summary>
/// Built-in UI extension for the Retry addon.
/// Shows retry configuration and status on the job detail page.
/// </summary>
public class RetryDashboardExtension : IWarpDashboardExtension
{
    public string Name => "retry";

    public Assembly ResourceAssembly => typeof(RetryDashboardExtension).Assembly;

    public string ResourceNamespace => "Warp.Dashboard.Extensions.Retry.dist";

    public DashboardExtensionManifest GetManifest()
    {
        return new DashboardExtensionManifest
        {
            Name = Name,
            ScriptUrl = $"/_ext/{Name}/index.js",
        };
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // No custom endpoints needed — retry data comes from job metadata
        // via the existing GET /detail/{id} endpoint.
    }
}

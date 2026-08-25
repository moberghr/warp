using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace Warp.Dashboard.Extensions;

/// <summary>
/// Implement this interface to extend the Warp dashboard UI.
/// Register as singleton in DI: services.AddSingleton&lt;IWarpDashboardExtension, MyExtension&gt;();
/// </summary>
public interface IWarpDashboardExtension
{
    /// <summary>
    /// Unique name for this extension (used in URL paths: /_ext/{name}/).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Assembly containing the embedded JS resources for this extension.
    /// </summary>
    Assembly ResourceAssembly { get; }

    /// <summary>
    /// Embedded resource namespace prefix for the extension's JS files.
    /// The runtime serves files from this namespace at /_ext/{Name}/.
    /// </summary>
    string ResourceNamespace { get; }

    /// <summary>
    /// Returns the UI manifest describing what this extension adds to the dashboard.
    /// </summary>
    DashboardExtensionManifest GetManifest();

    /// <summary>
    /// Register custom API endpoints for this extension.
    /// Endpoints are mapped under the Warp API group (e.g., /warp/api/ext/{name}/...).
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

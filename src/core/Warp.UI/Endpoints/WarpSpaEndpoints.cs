using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;
using Warp.UI.Extensions;

namespace Warp.UI.Endpoints;

/// <summary>
/// Serves the dashboard SPA: the shell (<c>index.html</c>) and its embedded static assets, including each
/// dashboard extension's JS.
/// </summary>
/// <remarks>
/// These are real endpoints rather than middleware so that <c>MapWarpUI(...).RequireAuthorization(...)</c>
/// gates them — a signed-out browser navigating to the dashboard is then challenged by ASP.NET, reaching
/// the host's sign-in, instead of being handed a bare 401 it cannot act on.
/// <para>
/// The assets are served here too, rather than by <c>UseStaticFiles</c>, because
/// <c>StaticFileMiddleware</c> deliberately stands down once routing has matched an endpoint — and the
/// shell's catch-all route matches every asset path. Leaving them on middleware meant every asset 404'd.
/// Serving them here keeps one owner for everything under the route prefix, and gates them uniformly.
/// Conditional requests and ranges still work: <c>Results.Stream</c> handles <c>If-None-Match</c> /
/// <c>If-Modified-Since</c> against the entity tag and timestamp passed below.
/// </para>
/// </remarks>
internal static class WarpSpaEndpoints
{
    private const string EmbeddedFileNamespace = "Warp.UI.dist";
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    internal static IEndpointConventionBuilder MapWarpSpa(this IEndpointRouteBuilder app, WarpUIOptions options, List<IWarpUIExtension> extensions, WarpDashboardGate gate)
    {
        var hasLogin = app.ServiceProvider.GetService<IWarpDashboardLoginMarker>() != null;
        var assets = new WarpSpaAssets(options.RoutePrefix, extensions);
        string[] methods = [HttpMethods.Get, HttpMethods.Head];

        // Typed as Delegate so overload resolution picks the route-handler overload rather than
        // RequestDelegate, which would discard the IResult instead of writing it (ASP0016).
        // The login flag is read per request, not captured here: whether the host replaced the login gate is
        // only settled once its conventions have been applied, which happens after this runs.
        Delegate handler = (HttpContext context) => Serve(context, options, assets, hasLogin && !gate.ReplacedByHostPolicy);

        var root = app.MapMethods(options.RoutePrefix, methods, handler);
        var rest = app.MapMethods($"{options.RoutePrefix}/{{**path}}", methods, handler);

        return new CompositeEndpointConventionBuilder([root, rest]);
    }

    private static async Task<IResult> Serve(HttpContext context, WarpUIOptions options, WarpSpaAssets assets, bool hasLogin)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // An asset request is one whose extension names a media type we could actually serve — NOT merely
        // any path containing a dot. SPA deep links routinely carry dots in their last segment: a job type
        // (/jobs/by-type/MyApp.Jobs.SendEmail), a webhook event (payment.completed), an adapter name. Those
        // must reach the shell so a refresh or bookmark works, while a genuinely missing asset still 404s
        // rather than answering HTML to a <script> tag.
        if (WarpSpaAssets.IsAssetRequest(path))
        {
            return assets.Resolve(path);
        }

        var html = await RenderIndexHtml(options, hasLogin, context.RequestAborted);

        return Results.Content(html, "text/html", Encoding.UTF8);
    }

    private static async Task<string> RenderIndexHtml(WarpUIOptions options, bool hasLogin, CancellationToken cancellationToken)
    {
        await using var stream = options.IndexStream();
        using var reader = new StreamReader(stream);

        var htmlString = await reader.ReadToEndAsync(cancellationToken);

        htmlString = htmlString.Replace("href=\"./", $"href=\"{options.RoutePrefix}/", StringComparison.Ordinal);
        htmlString = htmlString.Replace("src=\"./", $"src=\"{options.RoutePrefix}/", StringComparison.Ordinal);

        var headEndIndex = htmlString.IndexOf("</head>", StringComparison.Ordinal);

        // All host-supplied values are JSON-encoded so a stray quote or "</script>" can't break the
        // injected script (System.Text.Json's default HTML-safe encoder escapes < > & and emits a safe JS
        // string literal, or `null`). RoutePrefix is config, not user input, but encode it too for
        // consistency and defence in depth.
        var appSettingsString =
            $"<script> window.apiPath = {JsonValue(options.RoutePrefix + "/api/")}; window.basePath = {JsonValue(options.RoutePrefix)}; window.hasBuiltInLogin = {(hasLogin ? "true" : "false")};"
            + $" window.warpInstanceName = {JsonValue(options.InstanceName)};"
            + $" window.warpPortalUrl = {JsonValue(options.PortalUrl)};"
            + $" window.warpPortalLabel = {JsonValue(options.PortalLabel)};"
            + $" window.warpLogoUrl = {JsonValue(options.LogoUrl)};</script>";

        return htmlString.Insert(headEndIndex, appSettingsString);
    }

    // Emits a safe JS string literal (or `null`) for host-supplied branding values injected into the SPA.
    private static string JsonValue(string? value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// Maps a request path to the embedded file that backs it — the SPA bundle by default, or a dashboard
    /// extension's own assembly for paths under <c>{prefix}/_ext/{name}/</c>.
    /// </summary>
    private sealed class WarpSpaAssets
    {
        private readonly string _routePrefix;
        private readonly IFileProvider _spa;
        private readonly Dictionary<string, IFileProvider> _extensions;

        public WarpSpaAssets(string routePrefix, List<IWarpUIExtension> extensions)
        {
            _routePrefix = routePrefix;
            _spa = new EmbeddedFileProvider(typeof(WarpSpaEndpoints).Assembly, EmbeddedFileNamespace);
            _extensions = extensions.ToDictionary(
                x => $"{routePrefix}/_ext/{x.Name}/",
                x => (IFileProvider)new EmbeddedFileProvider(x.ResourceAssembly, x.ResourceNamespace),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether the path names something this serves: an extension that maps to a known media type.
        /// A dotted SPA route segment (<c>MyApp.Jobs.SendEmail</c>) maps to nothing and is not an asset.
        /// </summary>
        public static bool IsAssetRequest(string path) => ContentTypes.TryGetContentType(path, out _);

        public IResult Resolve(string path)
        {
            var (provider, subPath) = Locate(path);
            var file = provider.GetFileInfo(subPath);

            if (!file.Exists)
            {
                return Results.NotFound();
            }

            if (!ContentTypes.TryGetContentType(subPath, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            // Hashed filenames mean the content is immutable per build, so length + build timestamp is a
            // sufficient validator for the 304 the framework serves off it.
            var entityTag = new EntityTagHeaderValue($"\"{file.LastModified.ToUnixTimeSeconds():x}-{file.Length:x}\"");

            return Results.Stream(file.CreateReadStream(), contentType, lastModified: file.LastModified, entityTag: entityTag, enableRangeProcessing: true);
        }

        private (IFileProvider Provider, string SubPath) Locate(string path)
        {
            foreach (var (prefix, provider) in _extensions)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return (provider, path[prefix.Length..]);
                }
            }

            return (_spa, path[_routePrefix.Length..]);
        }
    }
}

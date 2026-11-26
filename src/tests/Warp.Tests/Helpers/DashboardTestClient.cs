using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Warp.Tests.Helpers;

public static class DashboardTestClient
{
    /// <summary>
    /// A test client that sends the header the dashboard's CSRF guard requires on state-changing API calls,
    /// the way the SPA's own HTTP client does.
    /// </summary>
    public static HttpClient GetDashboardTestClient(this WebApplication app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Warp-Request", "1");

        return client;
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Dashboard;
using Warp.Dashboard.Push;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// Auth integration tests for the dashboard SignalR hub. The hub is one of the endpoints
/// <c>MapWarpDashboard</c> returns, so whatever the host applies covers it too — no parallel auth code path.
/// <see cref="LocalRequests_NegotiateForbidden"/> is the one that earns its keep: endpoint filters do not
/// run for hub endpoints, so a filter-based gate would have left negotiate wide open.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardPushAuthTests
{
    private const string NegotiatePath = "/warp/api/hub/negotiate?negotiateVersion=1";

    [TimedFact]
    public async Task NoGate_NegotiateReturnsOk()
    {
        var (app, client) = await StartAsync();
        try
        {
            var response = await NegotiateAsync(client);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_NegotiateReturns401WithoutCookie()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            var response = await NegotiateAsync(client);

            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task BuiltInLogin_NegotiateReturns200WithCookie()
    {
        var (app, client) = await StartAsync(AddBuiltInLogin, x => x.RequireWarpDashboardLogin());
        try
        {
            using var form = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("username", "admin"),
                new KeyValuePair<string, string>("password", "admin"),
            ]);
            var login = await client.PostAsync("/warp/api/auth/login", form, XunitTestContext.Current.CancellationToken);
            login.StatusCode.ShouldBe(HttpStatusCode.OK);

            var cookie = login.Headers.GetValues("Set-Cookie").First().Split(';')[0];
            client.DefaultRequestHeaders.Add("Cookie", cookie);

            var response = await NegotiateAsync(client);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    [TimedFact]
    public async Task LocalRequests_NegotiateForbidden()
    {
        var (app, client) = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests());
        try
        {
            var response = await NegotiateAsync(client);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
        finally
        {
            await StopAsync(app, client);
        }
    }

    private static void AddBuiltInLogin(IServiceCollection services)
        => services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();

    private static async Task<HttpResponseMessage> NegotiateAsync(HttpClient client)
    {
        using var content = new ByteArrayContent([]);

        return await client.PostAsync(NegotiatePath, content, XunitTestContext.Current.CancellationToken);
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<IServiceCollection>? services = null,
        Action<WarpDashboardEndpointConventionBuilder>? gate = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<IDashboardPushMarker, DashboardPushMarker>();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        var warp = app.MapWarpDashboard("/warp");
        gate?.Invoke(warp);

        await app.StartAsync(XunitTestContext.Current.CancellationToken);

        var server = app.GetTestServer();
        var client = new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };

        return (app, client);
    }

    private static async Task StopAsync(WebApplication app, HttpClient client)
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    private sealed class TestCredentialValidator : IWarpCredentialValidator
    {
        public Task<bool> ValidateAsync(string username, string password)
            => Task.FromResult(
                string.Equals(username, "admin", StringComparison.Ordinal)
                && string.Equals(password, "admin", StringComparison.Ordinal));
    }
}

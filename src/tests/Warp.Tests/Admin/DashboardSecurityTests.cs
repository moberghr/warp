using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Dashboard;
using Warp.Dashboard.Extensions;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.Admin;

/// <summary>
/// What a caller outside the dashboard's trust boundary can reach: the CSRF request-header guard, the
/// loopback policy's host-name check, and the built-in login's throttle and cookie. Each test starts its own
/// host, because the throttle's state is per host and a test that locks a key out would leak into the next.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardSecurityTests
{
    private const string RequestHeader = "X-Warp-Request";
    private const string TestIpHeader = "X-Test-Ip";

    [TimedFact]
    public async Task StateChangingApiCall_WithoutRequestHeader_IsRejected()
    {
        await using var host = await StartAsync(x => x.AddWarpDashboard());

        // Never reaches the handler, which would need Warp's services: the guard answers first.
        using var response = await host.Client.PostAsync("/warp/api/jobs/failed/delete-by-type?type=x", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task StateChangingApiCall_WithRequestHeader_ReachesTheEndpoint()
    {
        await using var host = await StartAsync(x => x.AddWarpDashboard());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/warp/api/does-not-exist");
        request.Headers.Add(RequestHeader, "1");

        using var response = await host.Client.SendAsync(request, Ct);

        // The in-group catch-all answered, so the guard handed the request on to the endpoint.
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task SafeApiCall_WithoutRequestHeader_IsServed()
    {
        await using var host = await StartAsync(x => x.AddWarpDashboard());

        using var response = await host.Client.GetAsync("/warp/api/extensions", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedTheory]
    [InlineData(false, HttpStatusCode.BadRequest)]
    [InlineData(true, HttpStatusCode.NoContent)]
    public async Task ExtensionEndpointMappedWithARequestDelegate_IsGuardedToo(bool sendHeader, HttpStatusCode expected)
    {
        // Endpoint filters never run for a plain RequestDelegate endpoint, which is why the guard wraps the
        // delegate instead.
        await using var host = await StartAsync(x =>
        {
            x.AddWarpDashboard();
            x.AddSingleton<IWarpDashboardExtension, RequestDelegateExtension>();
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/warp/api/ext/raw/ping");
        if (sendHeader)
        {
            request.Headers.Add(RequestHeader, "1");
        }

        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [TimedFact]
    public async Task Login_WithoutRequestHeader_IsRejected()
    {
        await using var host = await StartAsync(AddBuiltInLogin);
        using var form = Credentials("admin", "admin");

        using var response = await host.Client.PostAsync("/warp/api/auth/login", form, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task Logout_WithoutRequestHeader_IsRejected()
    {
        await using var host = await StartAsync(AddBuiltInLogin);

        using var response = await host.Client.PostAsync("/warp/api/auth/logout", null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task LocalRequests_LoopbackConnectionWithForeignHostName_IsForbidden()
    {
        // DNS rebinding: the attacker's name resolves to 127.0.0.1, so the connection is loopback but the
        // browser sends the attacker's host name.
        await using var host = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests(), StampLoopback);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/warp");
        request.Headers.Host = "attacker.example:5000";

        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [TimedTheory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST:5000")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]:5000")]
    public async Task LocalRequests_LoopbackConnectionWithLoopbackHostName_IsServed(string hostName)
    {
        await using var host = await StartAsync(x => x.AddWarpDashboard(), x => x.RequireLocalRequests(), StampLoopback);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/warp");
        request.Headers.Host = hostName;

        using var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task Login_NonFormBody_Returns400()
    {
        await using var host = await StartAsync(AddBuiltInLogin);
        using var body = new StringContent("{\"username\":\"admin\"}", System.Text.Encoding.UTF8, "application/json");

        using var response = await PostLoginAsync(host.Client, body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [TimedFact]
    public async Task Login_SetsASecureCookieByDefault()
    {
        await using var host = await StartAsync(AddBuiltInLogin);

        using var response = await LoginAsync(host.Client, "admin", "admin");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").First().ShouldContain("secure", Case.Insensitive);
    }

    [TimedFact]
    public async Task Login_SecureCookieOptOut_SetsAPlainCookieOverHttp()
    {
        await using var host = await StartAsync(x => x.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>(y => y.RequireSecureCookie = false));

        using var response = await LoginAsync(host.Client, "admin", "admin");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Set-Cookie").First().ShouldNotContain("secure", Case.Insensitive);
    }

    [TimedFact]
    public async Task Login_RepeatedFailuresForOneUsername_AreThrottledFromAnyAddress()
    {
        await using var host = await StartAsync(AddThrottledLogin, pipeline: StampIpFromHeader);

        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.2")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The right password does not help: the validator is not even asked while the username is locked out.
        using var response = await LoginAsync(host.Client, "ADMIN", "admin", ip: "10.0.0.3");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [TimedFact]
    public async Task Login_RepeatedFailuresFromOneAddress_AreThrottledForAnyUsername()
    {
        await using var host = await StartAsync(AddThrottledLogin, pipeline: StampIpFromHeader);

        (await LoginAsync(host.Client, "alice", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "bob", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var response = await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.1");

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [TimedFact]
    public async Task Login_Success_ClearsTheCount()
    {
        await using var host = await StartAsync(AddThrottledLogin, pipeline: StampIpFromHeader);

        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [TimedFact]
    public async Task Login_LockoutEscalatesAndExpires()
    {
        await using var host = await StartAsync(AddThrottledLogin, pipeline: StampIpFromHeader);
        var time = host.App.Services.GetRequiredService<FakeTimeProvider>();

        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        time.Advance(TimeSpan.FromSeconds(30));

        // The first lockout is over; the next failure doubles it.
        (await LoginAsync(host.Client, "admin", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        time.Advance(TimeSpan.FromSeconds(30));
        using var locked = await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.1");
        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        locked.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(30));
        using var response = await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task Login_TrackingTableFullOfLockouts_RefusesNewKeysRatherThanForgettingALockout()
    {
        // Two keys fit. Both lock out on their first failure, so a third caller cannot be tracked without
        // evicting a lockout, which would let an attacker clear one by spraying new usernames.
        await using var host = await StartAsync(AddTinyThrottleTable, pipeline: StampIpFromHeader);

        (await LoginAsync(host.Client, "alice", "wrong", ip: "10.0.0.1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "bob", "wrong", ip: "10.0.0.2")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.3")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // The lockouts are still there.
        (await LoginAsync(host.Client, "alice", "wrong", ip: "10.0.0.4")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Once they expire, the old keys make room again.
        host.App.Services.GetRequiredService<FakeTimeProvider>().Advance(TimeSpan.FromSeconds(30));
        using var response = await LoginAsync(host.Client, "admin", "admin", ip: "10.0.0.3");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static CancellationToken Ct => XunitTestContext.Current.CancellationToken;

    private static void AddBuiltInLogin(IServiceCollection services)
        => services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>();

    // Two attempts before the first lockout; registered on a fake clock so expiry is driven by the test.
    private static void AddThrottledLogin(IServiceCollection services)
    {
        services.AddSingleton(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddSingleton<TimeProvider>(x => x.GetRequiredService<FakeTimeProvider>());
        services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>(x => x.Throttling.FreeAttempts = 2);
    }

    private static void AddTinyThrottleTable(IServiceCollection services)
    {
        services.AddSingleton(new FakeTimeProvider(DateTimeOffset.UtcNow));
        services.AddSingleton<TimeProvider>(x => x.GetRequiredService<FakeTimeProvider>());
        services.AddWarpDashboard().AddBuiltInLogin<TestCredentialValidator>(x =>
        {
            x.Throttling.FreeAttempts = 1;
            x.Throttling.MaxTrackedKeys = 2;
        });
    }

    private static void StampLoopback(WebApplication app)
        => app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            await next(context);
        });

    private static void StampIpFromHeader(WebApplication app)
        => app.Use(async (context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers[TestIpHeader].ToString());
            await next(context);
        });

    private static async Task<TestHost> StartAsync(
        Action<IServiceCollection> services,
        Action<WarpDashboardEndpointConventionBuilder>? gate = null,
        Action<WebApplication>? pipeline = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        services(builder.Services);

        var app = builder.Build();
        pipeline?.Invoke(app);

        if (app.Services.GetService<IAuthenticationSchemeProvider>() != null)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        var warp = app.MapWarpDashboard("/warp");
        gate?.Invoke(warp);

        await app.StartAsync(Ct);

        var server = app.GetTestServer();

        return new TestHost(app, new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress });
    }

    private static FormUrlEncodedContent Credentials(string username, string password)
        => new([
            new KeyValuePair<string, string>("username", username),
            new KeyValuePair<string, string>("password", password),
        ]);

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string username, string password, string? ip = null)
    {
        using var form = Credentials(username, password);

        return await PostLoginAsync(client, form, ip);
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, HttpContent body, string? ip = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/warp/api/auth/login") { Content = body };
        request.Headers.Add(RequestHeader, "1");
        if (ip != null)
        {
            request.Headers.Add(TestIpHeader, ip);
        }

        return await client.SendAsync(request, Ct);
    }

    private sealed class RequestDelegateExtension : IWarpDashboardExtension
    {
        public string Name => "raw";

        public System.Reflection.Assembly ResourceAssembly => typeof(RequestDelegateExtension).Assembly;

        public string ResourceNamespace => "none";

        public DashboardExtensionManifest GetManifest() => new() { Name = Name, ScriptUrl = string.Empty };

        public void MapEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapPost("ping", Ping);

        private static Task Ping(HttpContext context)
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;

            return Task.CompletedTask;
        }
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }
}

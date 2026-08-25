using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Warp.Dashboard;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.Admin;

/// <summary>
/// Host-supplied branding reaches the SPA as JSON-encoded window globals injected into the shell. These
/// assert the wire format rather than the rendering: the SPA reads exactly these names, and every value is
/// host-supplied, so a stray quote or a "&lt;/script&gt;" must not be able to break out of the script tag.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardBrandingTests
{
    [TimedFact]
    public async Task BrandName_WhenSet_IsInjectedForTheWordmarkAndTabTitle()
    {
        var shell = await GetShellAsync(o => o.BrandName = "Acme Jobs");

        shell.ShouldContain("window.warpBrandName = \"Acme Jobs\"");
    }

    [TimedFact]
    public async Task BrandName_WhenNotSet_IsNullSoTheSpaFallsBackToWarp()
    {
        var shell = await GetShellAsync(_ => { });

        shell.ShouldContain("window.warpBrandName = null");
    }

    [TimedFact]
    public async Task BrandName_WithScriptClosingTag_IsEscapedAndCannotBreakOutOfTheScript()
    {
        const string Payload = "</script><script>alert(1)</script>";

        var shell = await GetShellAsync(o => o.BrandName = Payload);

        // The value has to stay inside its JS string literal: System.Text.Json's HTML-safe encoder turns
        // every < and > into a \uXXXX escape, so nothing in a host-supplied name can close the tag early.
        // Asserting against the encoder's own output rather than a hand-written escape sequence keeps this
        // honest if the encoder's escaping ever changes shape.
        shell.ShouldNotContain(Payload);
        shell.ShouldContain($"window.warpBrandName = {JsonSerializer.Serialize(Payload)}");
    }

    [TimedFact]
    public async Task BrandName_IsIndependentOfInstanceName()
    {
        var shell = await GetShellAsync(o =>
        {
            o.BrandName = "Acme Jobs";
            o.InstanceName = "Production";
        });

        shell.ShouldContain("window.warpBrandName = \"Acme Jobs\"");
        shell.ShouldContain("window.warpInstanceName = \"Production\"");
    }

    private static async Task<string> GetShellAsync(Action<WarpDashboardOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        app.MapWarpDashboard(options =>
        {
            options.RoutePrefix = "/warp";
            configure(options);
        });

        await app.StartAsync(XunitTestContext.Current.CancellationToken);

        var server = app.GetTestServer();
        using var client = new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };

        return await client.GetStringAsync("/warp", XunitTestContext.Current.CancellationToken);
    }
}

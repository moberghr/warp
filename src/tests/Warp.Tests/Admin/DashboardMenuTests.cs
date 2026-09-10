using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Shouldly;
using Warp.Dashboard;
using XunitTestContext = Xunit.TestContext;

namespace Warp.Tests.Admin;

/// <summary>
/// The host-defined nav layout (<see cref="WarpDashboardOptions.ConfigureMenu"/>) reaches the SPA as a
/// JSON document injected into the shell. These assert the wire format and the fail-fast validation — the
/// SPA's own side (rendering order, the overflow group, unknown ids, divider normalisation) is covered by
/// navModel.test.ts.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardMenuTests
{
    [TimedFact]
    public async Task Menu_WhenNotConfigured_IsNullSoTheSpaKeepsItsDefaultNav()
    {
        var shell = await GetShellAsync(_ => { });

        shell.ShouldContain("window.warpMenu = null");
    }

    [TimedFact]
    public async Task Menu_SerializesOneOrderedSequenceOfPagesGroupsAndDividers()
    {
        // The bar is a sequence, not "pages then groups": a page declared after a group has to reach the
        // SPA after that group, or it cannot render there.
        var shell = await GetShellAsync(o => o.ConfigureMenu(m => m
            .Pages(WarpDashboardPage.Dashboard)
            .Group("Ops", WarpDashboardPage.Recurring, WarpDashboardPage.Issues)
            .Divider()
            .Pages(WarpDashboardPage.Jobs)));

        var entries = ReadMenu(shell).GetProperty("entries");

        entries.EnumerateArray().Select(Describe).ShouldBe(["page:dashboard", "group:Ops", "divider", "page:jobs"]);
        entries[1].GetProperty("pages").EnumerateArray().Select(x => x.GetString()).ShouldBe(["recurring", "issues"]);
    }

    [TimedFact]
    public async Task Menu_WithOnlyPages_DeclaresNoGroupsAndLeavesTheRestToTheOverflow()
    {
        // A bar-only layout is a first-class shape: N pages up front, everything else in one dropdown.
        // Declaring no groups must not read as "no layout", or the whole thing would be ignored.
        var shell = await GetShellAsync(o => o.ConfigureMenu(m => m.Pages(
            WarpDashboardPage.Dashboard,
            WarpDashboardPage.Jobs,
            WarpDashboardPage.Issues,
            WarpDashboardPage.Recurring,
            WarpDashboardPage.Applications)));

        var entries = ReadMenu(shell).GetProperty("entries");

        entries.GetArrayLength().ShouldBe(5);
        entries.EnumerateArray().ShouldAllBe(x => x.GetProperty("kind").GetString() == "page");
    }

    [TimedFact]
    public async Task Pages_CalledAgainAfterAGroup_AppendsRatherThanReordering()
    {
        // Pages, Group and Divider all append to one sequence, so a layout can be assembled in any order
        // and split across helpers — nothing is pinned to the left of the bar.
        var shell = await GetShellAsync(o => o.ConfigureMenu(m =>
        {
            m.Pages(WarpDashboardPage.Dashboard);
            m.Group("Ops", WarpDashboardPage.Issues);
            m.Pages(WarpDashboardPage.Jobs);
        }));

        ReadMenu(shell).GetProperty("entries").EnumerateArray().Select(Describe)
            .ShouldBe(["page:dashboard", "group:Ops", "page:jobs"]);
    }

    [TimedFact]
    public async Task ConfigureMenu_CalledTwice_KeepsAddingToTheSameLayout()
    {
        // A host splitting its setup across helpers must not have the second call silently replace the
        // first — the menu is one layout, not one call's worth of it.
        var shell = await GetShellAsync(o =>
        {
            o.ConfigureMenu(m => m.Group("Ops", WarpDashboardPage.Issues));
            o.ConfigureMenu(m => m.Group("Delivery", WarpDashboardPage.Webhooks));
        });

        ReadMenu(shell).GetProperty("entries").EnumerateArray().Select(Describe)
            .ShouldBe(["group:Ops", "group:Delivery"]);
    }

    [TimedFact]
    public async Task Menu_OverflowLabel_DefaultsToMoreAndIsOverridable()
    {
        var defaulted = ReadMenu(await GetShellAsync(o => o.ConfigureMenu(m => m.Group("Ops", WarpDashboardPage.Issues))));
        var overridden = ReadMenu(await GetShellAsync(o => o.ConfigureMenu(m =>
        {
            m.OverflowLabel = "Everything else";
            m.Group("Ops", WarpDashboardPage.Issues);
        })));

        defaulted.GetProperty("overflowLabel").GetString().ShouldBe("More");
        overridden.GetProperty("overflowLabel").GetString().ShouldBe("Everything else");
    }

    [TimedFact]
    public async Task Menu_GroupLabelWithScriptClosingTag_IsEscapedAndCannotBreakOutOfTheScript()
    {
        const string Payload = "</script><script>alert(1)</script>";

        var shell = await GetShellAsync(o => o.ConfigureMenu(m => m.Group(Payload, WarpDashboardPage.Issues)));

        // The label is host-supplied, so it has to stay inside the injected JSON: System.Text.Json's
        // HTML-safe encoder turns every < and > into a \uXXXX escape.
        shell.ShouldNotContain(Payload);
        ReadMenu(shell).GetProperty("entries")[0].GetProperty("label").GetString().ShouldBe(Payload);
    }

    [TimedFact]
    public void Group_WithAPageAlreadyPlaced_Throws()
    {
        var menu = new WarpDashboardMenu();
        menu.Group("Ops", WarpDashboardPage.Issues);

        // Placed twice it would render twice, and the active-page highlight would have two candidates.
        Should.Throw<ArgumentException>(() => menu.Group("Health", WarpDashboardPage.Issues));
    }

    [TimedFact]
    public void Pages_WithAPageAlreadyPlacedInAGroup_Throws()
    {
        var menu = new WarpDashboardMenu();
        menu.Group("Ops", WarpDashboardPage.Issues);

        // Appending is fine; placing the same page twice is not, wherever the two calls put it.
        Should.Throw<ArgumentException>(() => menu.Pages(WarpDashboardPage.Issues));
    }

    [TimedFact]
    public async Task Group_NamedLikeTheOverflowGroup_ThrowsAtStartup()
    {
        // Both would render under one label, and the SPA keys its open-dropdown state on the label: two
        // same-named triggers read as open together and the panel resolves to the first, leaving the
        // overflow group's pages unreachable from the nav. Checked at map time, not at Group() time, so
        // it catches an OverflowLabel set after the group as well.
        var mapping = MapAsync(o => o.ConfigureMenu(m =>
        {
            m.Group("More", WarpDashboardPage.Counters);
            m.Pages(WarpDashboardPage.Dashboard);
        }));

        var ex = await Should.ThrowAsync<InvalidOperationException>(mapping);
        ex.Message.ShouldContain("OverflowLabel");

        // The same layout is fine once the two names differ.
        await MapAsync(o => o.ConfigureMenu(m =>
        {
            m.OverflowLabel = "Everything else";
            m.Group("More", WarpDashboardPage.Counters);
        }));
    }

    /// <summary>
    /// Blanking the label does not escape the collision check. Serialize() substitutes "More" for a
    /// blank OverflowLabel, so validating the RAW value let this shape through and then shipped an
    /// overflow group sharing a name with a declared one — the very collision the check exists to
    /// prevent, and unreachable-by-nav pages in the SPA. Both sides read the effective label now.
    /// </summary>
    [TimedTheory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Group_NamedLikeTheOverflowGroupDefault_ThrowsEvenWhenOverflowLabelIsBlank(string blank)
    {
        var mapping = MapAsync(o => o.ConfigureMenu(m =>
        {
            m.OverflowLabel = blank;
            m.Group("More", WarpDashboardPage.Counters);
        }));

        var ex = await Should.ThrowAsync<InvalidOperationException>(mapping);
        ex.Message.ShouldContain("OverflowLabel");
    }

    /// <summary>
    /// The blank fallback is also what the SPA receives, so the rendered label and the validated one
    /// are the same string.
    /// </summary>
    [TimedFact]
    public async Task Menu_BlankOverflowLabel_SerializesTheDefault()
    {
        var menu = ReadMenu(await GetShellAsync(o => o.ConfigureMenu(m =>
        {
            m.OverflowLabel = "  ";
            m.Group("Ops", WarpDashboardPage.Issues);
        })));

        menu.GetProperty("overflowLabel").GetString().ShouldBe("More");
    }

    [TimedFact]
    public void Group_WithADuplicateLabel_Throws()
    {
        var menu = new WarpDashboardMenu();
        menu.Group("Ops", WarpDashboardPage.Issues);

        Should.Throw<ArgumentException>(() => menu.Group("ops", WarpDashboardPage.Recurring));
    }

    [TimedFact]
    public void Group_WithNoPagesOrNoLabel_Throws()
    {
        Should.Throw<ArgumentException>(() => new WarpDashboardMenu().Group("Ops"));
        Should.Throw<ArgumentException>(() => new WarpDashboardMenu().Group(" ", WarpDashboardPage.Issues));
    }

    [TimedFact]
    public async Task EveryPage_HasADistinctId_TheSpaCanJoinOn()
    {
        // The ids are a wire format shared with navModel.ts, which mirrors them in its NavPageId union.
        // A member reaching the SPA with no id (or one colliding with another page's) is a layout entry
        // that goes nowhere. Placing every member also proves none of them throws on the way out — an id
        // map missing a member fails here rather than at a host's startup.
        var shell = await GetShellAsync(o => o.ConfigureMenu(m => m.Group("All", Enum.GetValues<WarpDashboardPage>())));

        var ids = ReadMenu(shell).GetProperty("entries")[0].GetProperty("pages")
            .EnumerateArray()
            .Select(x => x.GetString())
            .ToList();

        ids.Count.ShouldBe(Enum.GetValues<WarpDashboardPage>().Length);
        ids.ShouldAllBe(x => !string.IsNullOrWhiteSpace(x));
        ids.Distinct(StringComparer.Ordinal).Count().ShouldBe(ids.Count);
    }

    private static string Describe(JsonElement entry)
    {
        var kind = entry.GetProperty("kind").GetString();

        return kind switch
        {
            "page" => $"page:{entry.GetProperty("page").GetString()}",
            "group" => $"group:{entry.GetProperty("label").GetString()}",
            _ => kind!,
        };
    }

    private static JsonElement ReadMenu(string shell)
    {
        const string Marker = "window.warpMenu = ";
        var start = shell.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        var json = shell[start..shell.IndexOf(";</script>", start, StringComparison.Ordinal)];

        return JsonDocument.Parse(json).RootElement;
    }

    private static async Task MapAsync(Action<WarpDashboardOptions> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();

        app.MapWarpDashboard(options =>
        {
            options.RoutePrefix = "/warp";
            configure(options);
        });
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

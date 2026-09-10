using System.Text.Json;
using System.Text.Json.Serialization;

namespace Warp.Dashboard;

/// <summary>
/// Host-defined layout for the dashboard's nav bar: an ordered sequence of pages, group dropdowns and
/// dividers, rendered left to right exactly as declared.
/// </summary>
/// <remarks>
/// <para>
/// Leave it untouched and the SPA renders its own default layout, unchanged. Declare anything and the bar
/// becomes the host's: <see cref="Pages"/>, <see cref="Group"/> and <see cref="Divider"/> all append to one
/// sequence, so a page may sit between two groups, and calls may be split across helpers or interleaved
/// freely.
/// </para>
/// <para>
/// A page the layout never mentions is not hidden — it lands in one trailing overflow group
/// (<see cref="OverflowLabel"/>), so every page the deployment actually has stays reachable. Pages whose
/// addon the host has not registered are dropped by the SPA the same way they are today, wherever the
/// layout put them.
/// </para>
/// </remarks>
public sealed class WarpDashboardMenu
{
    private readonly List<EntrySpec> _entries = [];
    private readonly HashSet<WarpDashboardPage> _placed = [];
    private readonly HashSet<string> _labels = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The label used when the host leaves <see cref="OverflowLabel"/> unset or blank.
    /// </summary>
    private const string DefaultOverflowLabel = "More";

    /// <summary>
    /// Label for the trailing group collecting every page the layout did not place. Defaults to "More";
    /// blank or whitespace falls back to it too, and the fallback is what the collision check validates.
    /// </summary>
    public string OverflowLabel { get; set; } = DefaultOverflowLabel;

    /// <summary>Whether the host defined a layout at all. False means the SPA keeps its default nav.</summary>
    internal bool IsConfigured => _entries.Count > 0;

    /// <summary>
    /// The label the overflow group actually renders under, with the blank fallback applied.
    /// </summary>
    /// <remarks>
    /// <see cref="Validate"/> and <see cref="Serialize"/> must both read THIS rather than
    /// <see cref="OverflowLabel"/> directly, or they disagree about what the overflow group is called:
    /// blanking the label and declaring a group named "More" passed validation against the raw empty
    /// string and then shipped a group colliding with the substituted default — the exact collision the
    /// check exists to prevent.
    /// </remarks>
    private string EffectiveOverflowLabel =>
        string.IsNullOrWhiteSpace(OverflowLabel) ? DefaultOverflowLabel : OverflowLabel;

    /// <summary>
    /// Appends pages that sit directly on the bar rather than inside a group, in the order given.
    /// Declaring any replaces the default pair (Dashboard, Jobs) — list them explicitly to keep them.
    /// </summary>
    public WarpDashboardMenu Pages(params WarpDashboardPage[] pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        foreach (var page in pages)
        {
            Claim(page);
            _entries.Add(new EntrySpec { Kind = "page", Page = WarpDashboardPageIds.For(page) });
        }

        return this;
    }

    /// <summary>
    /// Appends a group dropdown holding <paramref name="pages"/>, in the order given.
    /// </summary>
    public WarpDashboardMenu Group(string label, params WarpDashboardPage[] pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(pages);

        if (pages.Length == 0)
        {
            throw new ArgumentException($"Group \"{label}\" lists no pages. A group with nothing in it would never render.", nameof(pages));
        }

        if (!_labels.Add(label))
        {
            throw new ArgumentException($"Group \"{label}\" was already declared. List all of its pages in one call.", nameof(label));
        }

        foreach (var page in pages)
        {
            Claim(page);
        }

        _entries.Add(new EntrySpec { Kind = "group", Label = label, Pages = [.. pages.Select(WarpDashboardPageIds.For)] });

        return this;
    }

    /// <summary>
    /// Appends a vertical rule at this point in the bar, for grouping entries visually.
    /// </summary>
    /// <remarks>
    /// The default nav draws one between its two direct pages and its group triggers; once the layout is
    /// yours, that boundary no longer exists, so place the rules you want. One that ends up leading,
    /// trailing, or beside another — because addon gating removed what stood next to it — is dropped, so a
    /// rule can never dangle off the end of the bar.
    /// </remarks>
    public WarpDashboardMenu Divider()
    {
        _entries.Add(new EntrySpec { Kind = "divider" });

        return this;
    }

    /// <summary>
    /// Fail-fast checks that can only run once the whole layout is declared. Called by
    /// <c>MapWarpDashboard</c> at startup — never from <see cref="Serialize"/>, which runs per shell
    /// request, where a throw would 500 every dashboard load instead of failing the host's boot.
    /// </summary>
    internal void Validate()
    {
        if (!IsConfigured)
        {
            return;
        }

        // The overflow group renders under this label, so a group already using it would collide: the SPA
        // keys its open-dropdown state on the label, so two same-named triggers both read as open and the
        // panel always resolves to the first — leaving the overflow group's pages unreachable from the
        // nav, which is the one guarantee this whole design makes. Reserved unconditionally rather than
        // only when something is actually left over, so the rule doesn't depend on how many pages a
        // future Warp version ships.
        if (_labels.Contains(EffectiveOverflowLabel))
        {
            throw new InvalidOperationException(
                $"Group \"{EffectiveOverflowLabel}\" collides with the menu's overflow group, which every unplaced page falls into. "
                + $"Rename the group or set {nameof(OverflowLabel)} to something else.");
        }
    }

    /// <summary>
    /// The layout as the SPA reads it, or null when the host defined none (SPA keeps its own default).
    /// </summary>
    internal string? Serialize()
    {
        if (!IsConfigured)
        {
            return null;
        }

        var spec = new MenuSpec
        {
            Entries = _entries,
            OverflowLabel = EffectiveOverflowLabel,
        };

        // Web defaults for camelCase, and their HTML-safe encoder so a host-supplied group label
        // carrying "</script>" stays inside its JS string literal when the shell injects this.
        return JsonSerializer.Serialize(spec, JsonOptions);
    }

    // A page in two places would render twice and make the active-page highlight ambiguous. Caught at the
    // declaring call so the message names the entry that collided, not just the page.
    private void Claim(WarpDashboardPage page)
    {
        // Validates the member as a side effect: an undefined enum value has no id and throws here, at
        // startup, rather than silently serializing an id nothing in the SPA matches.
        _ = WarpDashboardPageIds.For(page);

        if (!_placed.Add(page))
        {
            throw new ArgumentException($"Page {page} is already placed in the menu. Each page belongs in exactly one place.", nameof(page));
        }
    }

    internal sealed class MenuSpec
    {
        public required IReadOnlyList<EntrySpec> Entries { get; init; }

        public required string OverflowLabel { get; init; }
    }

    internal sealed class EntrySpec
    {
        public required string Kind { get; init; }

        public string? Page { get; init; }

        public string? Label { get; init; }

        public IReadOnlyList<string>? Pages { get; init; }
    }
}

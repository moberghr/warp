using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Warp.Core.Enums;

namespace Warp.Core.ErrorGrouping;

/// <summary>
/// The pure grouping function (§8.29). Given an error's source, type, and locus it produces a stable 32-hex
/// fingerprint; it also normalizes a message to a PII-safe Title and extracts the top in-app stack frame. Used
/// both by <c>ErrorGroupAggregator</c> (off the hot path) and on-read by the source detail pages to link an
/// occurrence back to its issue — the same deterministic function, so both agree.
/// </summary>
public static partial class ErrorFingerprint
{
    /// <summary>Frames whose symbol starts with one of these are framework/plumbing, not "in-app" (default; configurable).</summary>
    public static readonly IReadOnlyList<string> DefaultInAppDenylist =
    [
        "Warp.",
        "System.",
        "Microsoft.",
        "Npgsql.",
    ];

    /// <summary>
    /// Stable identity for an exception group: <c>hash(source + type + locus)</c>. The message is deliberately
    /// NOT included (message-varying occurrences group); <paramref name="locus"/> is the top in-app frame when
    /// present, else the culprit.
    /// </summary>
    public static string Compute(ErrorSource source, string type, string locus)
    {
        var canonical = $"{(int)source}|{type}|{locus}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Identity for an endpoint status-code group (4xx): <c>hash(status + route)</c>, no exception involved.</summary>
    public static string ComputeForStatusCode(int statusCode, string route)
        => Compute(ErrorSource.Endpoint, $"HTTP {statusCode}", route);

    /// <summary>
    /// Collapse the variable parts of a message so occurrences group and the result is PII-safe: GUIDs, quoted
    /// literals, long hex runs, and bare numbers become placeholders. Becomes the group Title.
    /// </summary>
    public static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var text = message.Trim();
        text = GuidPattern().Replace(text, "<guid>");
        text = QuotedPattern().Replace(text, "<str>");
        text = HexPattern().Replace(text, "<hex>");
        text = NumberPattern().Replace(text, "<num>");

        return text.Length <= 300 ? text : text[..300];
    }

    /// <summary>
    /// The top "in-app" frame of a stack (the fine-grouping locus). Walks frames top-down and returns the first
    /// whose symbol isn't matched by <paramref name="inAppDenylist"/>. Handles both .NET (<c>at Ns.Type.Method(..)
    /// in file:line N</c>) and browser (<c>at fn (url:line:col)</c> / <c>fn@url:line:col</c>) shapes. Null when no
    /// frame is parseable — the caller falls back to the culprit.
    /// </summary>
    public static string? ExtractTopFrame(string? stack, IReadOnlyCollection<string> inAppDenylist)
    {
        if (string.IsNullOrWhiteSpace(stack))
        {
            return null;
        }

        foreach (var rawLine in stack.Split('\n'))
        {
            var symbol = ParseFrameSymbol(rawLine.Trim());
            if (symbol is null)
            {
                continue;
            }

            if (IsFrameworkFrame(symbol, inAppDenylist))
            {
                continue;
            }

            return symbol;
        }

        return null;
    }

    private static string? ParseFrameSymbol(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }

        // Browser frames ("at fn (https://…/File.tsx:42:18)", "fn@https://…/File.tsx:42:18") also start with
        // "at " and contain dots, so detect them FIRST by their URL/'@'/path markers and reduce to the file.
        if (LooksLikeBrowserFrame(line))
        {
            return ExtractBrowserLocation(line);
        }

        // .NET: "at Namespace.Type.Method(args) in File.cs:line 42" → "Namespace.Type.Method".
        if (line.StartsWith("at ", StringComparison.Ordinal) && line.Contains('.', StringComparison.Ordinal))
        {
            var body = line[3..];
            var paren = body.IndexOf('(', StringComparison.Ordinal);
            if (paren > 0)
            {
                return body[..paren].Trim();
            }
        }

        return null;
    }

    private static bool LooksLikeBrowserFrame(string line)
    {
        if (line.Contains("://", StringComparison.Ordinal) || line.AsSpan().IndexOf('@') >= 0)
        {
            return true;
        }

        var open = line.LastIndexOf('(');
        var close = line.LastIndexOf(')');

        return open >= 0 && close > open && line[(open + 1)..close].Contains('/', StringComparison.Ordinal);
    }

    private static string? ExtractBrowserLocation(string line)
    {
        var span = line;
        var hasLocation = false;

        var open = span.LastIndexOf('(');
        var close = span.LastIndexOf(')');
        if (open >= 0 && close > open)
        {
            span = span[(open + 1)..close];
            hasLocation = true;
        }
        else
        {
            var at = span.AsSpan().IndexOf('@');
            if (at >= 0)
            {
                span = span[(at + 1)..];
                hasLocation = true;
            }
        }

        // Require real frame evidence — a paren/'@' location, or a bare trailing :line:col (Safari style). This
        // stops the leading "ExceptionType: message" line of a stack from being mistaken for a frame.
        if (!hasLocation && !FrameLineColPattern().IsMatch(span))
        {
            return null;
        }

        // Strip a trailing :line:col, then reduce a URL/path to its basename.
        span = FrameLineColPattern().Replace(span, string.Empty);
        var lastSlash = span.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0)
        {
            span = span[(lastSlash + 1)..];
        }

        var query = span.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            span = span[..query];
        }

        span = span.Trim();

        return span.Length > 0 && span.Contains('.', StringComparison.Ordinal) ? span : null;
    }

    private static bool IsFrameworkFrame(string symbol, IReadOnlyCollection<string> inAppDenylist)
        => inAppDenylist.Any(prefix => symbol.StartsWith(prefix, StringComparison.Ordinal));

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.ExplicitCapture, 250)]
    private static partial Regex GuidPattern();

    [GeneratedRegex("'[^']*'|\"[^\"]*\"", RegexOptions.ExplicitCapture, 250)]
    private static partial Regex QuotedPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8,}\b", RegexOptions.ExplicitCapture, 250)]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\b\d+\b", RegexOptions.ExplicitCapture, 250)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@":\d+(?::\d+)?$", RegexOptions.ExplicitCapture, 250)]
    private static partial Regex FrameLineColPattern();
}

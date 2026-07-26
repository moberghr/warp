using System.Text.Json;

namespace Warp.Http.ClientObservability;

/// <summary>
/// Wire contract for a batch POSTed to the client ingest endpoint (§8.27). The public key travels in the
/// <c>x-warp-key</c> header (not the body). Deserialised with the ASP.NET web defaults (camelCase,
/// case-insensitive).
/// </summary>
public sealed class ClientIngestBatch
{
    /// <summary>The DSN key when sent in the body (a <c>sendBeacon</c> caller cannot set the <c>x-warp-key</c> header). The header takes precedence when both are present.</summary>
    public string? Key { get; set; }

    public string? Session { get; set; }

    public string? Release { get; set; }

    public List<ClientIngestEvent>? Events { get; set; }
}

/// <summary>One event in a client ingest batch. <c>type</c> is error|vital|log|event; <c>ts</c> is unix-ms.</summary>
public sealed class ClientIngestEvent
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? Level { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    public double? Value { get; set; }

    public string? Url { get; set; }

    /// <summary>W3C trace id (32-hex, no hyphens) the browser propagated for a request event; parsed to a Guid server-side to join the server trace.</summary>
    public string? TraceId { get; set; }

    public long? Ts { get; set; }

    public JsonElement? Props { get; set; }

    public JsonElement? Breadcrumbs { get; set; }
}

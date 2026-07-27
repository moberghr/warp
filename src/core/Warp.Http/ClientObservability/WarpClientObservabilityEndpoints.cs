using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Warp.Core.ClientObservability;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Http.ClientObservability;

/// <summary>
/// Maps the public browser ingest endpoint (§8.27): <c>POST {IngestPath}</c> accepts a batch of client events,
/// <c>GET {IngestPath}/client.js</c> serves the shipped browser script. Auth is a public write-only DSN key
/// (<c>x-warp-key</c> → the trusted application name); a CORS origin allowlist, an in-memory per-key rate
/// limit, and hard size/batch caps guard the public surface. Recording is lossy — a full buffer drops
/// (<c>warp.client.events.dropped</c>); the browser is never blocked or failed. Requires
/// <c>AddClientObservability()</c> in the <c>AddWarp</c> lambda.
/// </summary>
public static class WarpClientObservabilityEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapWarpClientObservability(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.ServiceProvider.GetService<IOptions<WarpClientObservabilityOptions>>()?.Value
            ?? throw new InvalidOperationException(
                "MapWarpClientObservability() requires AddClientObservability() to have been called inside the "
                + "AddWarp<TContext>() configuration lambda.");

        var path = options.IngestPath;

        app.MapPost(path, (Delegate)IngestAsync).AllowAnonymous();
        app.MapMethods(path, ["OPTIONS"], (Delegate)Preflight).AllowAnonymous();
        app.MapGet(path + "/client.js", (Delegate)ServeScript).AllowAnonymous();

        return app;
    }

    private static IResult ServeScript() => Results.Text(WarpClientScript.Content, "text/javascript; charset=utf-8");

    private static IResult Preflight(HttpContext ctx)
    {
        var options = ctx.RequestServices.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value;
        ApplyCors(ctx, options);
        ctx.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
        ctx.Response.Headers.AccessControlAllowHeaders = "content-type, x-warp-key";

        return Results.NoContent();
    }

    private static async Task<IResult> IngestAsync(HttpContext ctx)
    {
        var options = ctx.RequestServices.GetRequiredService<IOptions<WarpClientObservabilityOptions>>().Value;

        // Ingest disabled unless at least one DSN key is configured (safe default).
        if (options.IngestKeys.Count == 0)
        {
            return Results.NotFound();
        }

        // Origin allowlist. A missing Origin is a non-browser/same-origin caller; a same-origin request
        // (Origin == this server's scheme://host) needs no CORS grant. Cross-origin must be allowlisted.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin)
            && !string.Equals(origin, $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase)
            && !options.AllowedOrigins.Contains(origin))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        ApplyCors(ctx, options);

        // Rate-limit per CALLER IP, BEFORE the body read/parse — the DSN key is public (shipped in the bundle),
        // so keying on it lets any holder exhaust the shared budget; the IP is the meaningful abuse dimension.
        // Checking here (one token per request) bounds floods of bad-key / empty / oversized posts cheaply.
        var limiter = ctx.RequestServices.GetRequiredService<ClientIngestRateLimiter>();
        var rateKey = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!limiter.TryAcquire(rateKey, 1))
        {
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (ctx.Request.ContentLength is long declared && declared > options.MaxIngestBytes)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var body = await ReadCappedAsync(ctx.Request.Body, options.MaxIngestBytes, ctx.RequestAborted);
        if (body is null)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        ClientIngestBatch? batch;
        try
        {
            batch = JsonSerializer.Deserialize<ClientIngestBatch>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }

        // DSN key → trusted application (never client-declared). Header wins; body is the sendBeacon fallback.
        var key = ctx.Request.Headers["x-warp-key"].ToString();
        if (string.IsNullOrEmpty(key))
        {
            key = batch?.Key ?? string.Empty;
        }

        if (string.IsNullOrEmpty(key) || !options.IngestKeys.TryGetValue(key, out var application))
        {
            return Results.Unauthorized();
        }

        var events = batch?.Events;
        if (events is null || events.Count == 0)
        {
            return Results.NoContent();
        }

        var recorder = ctx.RequestServices.GetService<IClientEventRecorder>();
        var time = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow().UtcDateTime;

        var remoteIp = options.CaptureRemoteIp ? ctx.Connection.RemoteIpAddress?.ToString() : null;
        var userAgent = options.CaptureUserAgent ? Truncate(ctx.Request.Headers.UserAgent.ToString(), 1024) : null;

        var accepted = Math.Min(events.Count, options.MaxEventsPerBatch);
        if (events.Count > accepted)
        {
            // Batch-cap truncation is a drop too — count it so it isn't silent (mirrors the buffer-full and
            // unrecognized-type drops below).
            WarpTelemetry.ClientEventsDropped.Add(events.Count - accepted);
        }

        for (var i = 0; i < accepted; i++)
        {
            var record = BuildRecord(events[i], batch!, application, remoteIp, userAgent, options, now);
            if (record is null)
            {
                // Unrecognized event type — dropped, but counted so it isn't silent (mirrors the buffer-full drop).
                WarpTelemetry.ClientEventsDropped.Add(1);

                continue;
            }

            // Meters are always-on (independent of sink §8.24); recorder is absent under Otel-only.
            WarpTelemetry.RecordClientEvent(record.Type, application);

            // Only allowlisted Core Web Vitals become a meter tag — an arbitrary browser-sent vital name must
            // never explode meter cardinality on this public endpoint (§1.2 / §8.19).
            if (record.Type == ClientEventType.Vital && record.Value.HasValue && record.Name is not null && ClientEventCardinality.KnownVitals.Contains(record.Name))
            {
                WarpTelemetry.RecordClientVital(record.Name.ToUpperInvariant(), record.Value.Value, application);
            }

            if (recorder is not null && !recorder.Record(record))
            {
                WarpTelemetry.ClientEventsDropped.Add(1);
            }
        }

        return Results.NoContent();
    }

    private static ClientEventRecord? BuildRecord(
        ClientIngestEvent evt,
        ClientIngestBatch batch,
        string application,
        string? remoteIp,
        string? userAgent,
        WarpClientObservabilityOptions options,
        DateTime now)
    {
        if (!TryParseType(evt.Type, out var type))
        {
            return null;
        }

        var max = options.MaxCapturedBodySize;

        return new ClientEventRecord
        {
            Application = application,
            Type = type,
            Name = Truncate(evt.Name, 512),
            Level = Truncate(evt.Level, 32),
            Message = Truncate(evt.Message, max),
            Stack = Truncate(evt.Stack, max),
            Value = evt.Value,
            Url = Truncate(evt.Url, 2048),
            TraceId = ParseTraceId(evt.TraceId),
            SessionId = Truncate(batch.Session, 128),
            Release = Truncate(batch.Release, 128),
            UserAgent = userAgent,
            RemoteIp = remoteIp,
            Properties = RedactAndSerialize(evt.Props, options.RedactedKeys, max),
            Breadcrumbs = Truncate(Serialize(evt.Breadcrumbs), max),
            Timestamp = ClampTimestamp(evt.Ts, now),
        };
    }

    // The browser sends a W3C trace id as 32 lowercase hex chars; the server stores/joins it as a Guid in the
    // same "N" form used by EndpointCallLog.TraceId / Job.TraceId. Bad/short values (or the all-zero invalid
    // trace id) degrade to null rather than fault the request.
    private static Guid? ParseTraceId(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || !Guid.TryParseExact(raw, "N", out var traceId) || traceId == Guid.Empty)
        {
            return null;
        }

        return traceId;
    }

    private static bool TryParseType(string? raw, out ClientEventType type)
    {
        switch (raw?.ToUpperInvariant())
        {
            case "ERROR": type = ClientEventType.Error; return true;
            case "VITAL": type = ClientEventType.Vital; return true;
            case "LOG": type = ClientEventType.Log; return true;
            case "EVENT": type = ClientEventType.Event; return true;
            case "REQUEST": type = ClientEventType.Request; return true;
            default: type = default; return false;
        }
    }

    private static void ApplyCors(HttpContext ctx, WarpClientObservabilityOptions options)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && options.AllowedOrigins.Contains(origin))
        {
            ctx.Response.Headers.AccessControlAllowOrigin = origin;
            ctx.Response.Headers.Vary = "Origin";
        }
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream body, int max, CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }

        return buffer.ToArray();
    }

    private static string? RedactAndSerialize(JsonElement? props, ISet<string> denylist, int maxBytes)
    {
        if (props is null || props.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return Truncate(JsonSerializer.Serialize(Redact(props.Value, denylist), JsonOptions), maxBytes);
    }

    // Apply the denylist at EVERY nesting level — a secret named `password`/`authorization` nested one or more
    // levels deep (warp.track('x', { user: { password: '…' } })) must redact too, not just top-level keys (§1.2).
    // Depth is bounded by System.Text.Json's parse-time max depth, so the recursion can't be driven unbounded.
    private static object? Redact(JsonElement element, ISet<string> denylist)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                map[property.Name] = denylist.Contains(property.Name) ? "[redacted]" : Redact(property.Value, denylist);
            }

            return map;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Select(x => Redact(x, denylist)).ToList();
        }

        return element;
    }

    private static string? Serialize(JsonElement? element)
        => element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            ? null
            : element.Value.GetRawText();

    private static DateTime ClampTimestamp(long? unixMs, DateTime now)
    {
        if (unixMs is null)
        {
            return now;
        }

        // A crafted out-of-range value (e.g. long.MaxValue) throws — never let it fault the request (the
        // ingest path must not fail the caller, §8.27); fall back to now.
        DateTime ts;
        try
        {
            ts = DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return now;
        }

        // Reject nonsense client clocks: no further back than a day, no further ahead than 5 minutes.
        if (ts < now.AddDays(-1) || ts > now.AddMinutes(5))
        {
            return now;
        }

        return ts;
    }

    private static string? Truncate(string? value, int maxBytes)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        // Trim to a char boundary that fits the byte budget.
        var count = value.Length;
        while (count > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, count)) > maxBytes)
        {
            count--;
        }

        return value[..count];
    }
}

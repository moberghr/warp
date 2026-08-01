using Warp.Core.Enums;

namespace Warp.Core.Services;

/// <summary>
/// Read side of client (browser) observability (§8.27), registered by <c>AddWarp</c> itself so dashboard-only /
/// publisher-only processes resolve it. The summary is computed from the durable <c>clientevent:</c> Counter
/// fold (survives raw <c>ClientEventLog</c> cleanup, §8.22); the event stream + detail read raw rows and
/// degrade to empty once swept.
/// </summary>
public interface IClientEventQueryService
{
    Task<ClientObservabilitySummaryModel> GetSummary(string? application, CancellationToken ct);

    Task<ClientEventPageModel> GetEvents(ClientEventFilter filter, CancellationToken ct);

    Task<ClientEventDetailModel?> GetEvent(Guid id, CancellationToken ct);

    Task<IReadOnlyList<string>> GetApplications(CancellationToken ct);

    /// <summary>
    /// The unified timeline for one browser session (§8.27): the session's client events merged chronologically
    /// with the server endpoint calls they triggered (joined by the request events' trace ids), so a session's
    /// client and server activity read as one page. Null when the session has no (unswept) client events.
    /// </summary>
    Task<ClientSessionModel?> GetSession(string sessionId, CancellationToken ct);
}

public sealed class ClientEventFilter
{
    public string? Application { get; init; }

    public ClientEventType? Type { get; init; }

    public string? SessionId { get; init; }

    public int Page { get; init; }

    public int PageSize { get; init; } = 50;
}

public sealed class ClientObservabilitySummaryModel
{
    public string? Application { get; init; }

    public long ErrorCount { get; init; }

    public long LogCount { get; init; }

    public long EventCount { get; init; }

    public long VitalCount { get; init; }

    /// <summary>Errors ÷ total events across all types (0 when no events).</summary>
    public double ErrorRate { get; init; }

    public IReadOnlyList<ClientNameCountModel> TopErrors { get; init; } = [];

    public IReadOnlyList<ClientNameCountModel> TopEvents { get; init; } = [];

    public IReadOnlyList<ClientVitalStatModel> Vitals { get; init; } = [];

    public IReadOnlyList<ClientHistoryPointModel> History { get; init; } = [];
}

public sealed class ClientNameCountModel
{
    public string Name { get; init; } = string.Empty;

    public long Count { get; init; }
}

public sealed class ClientVitalStatModel
{
    public string Name { get; init; } = string.Empty;

    public long SampleCount { get; init; }

    public double AvgValue { get; init; }

    /// <summary>The p75 value — Google's Core-Web-Vitals percentile.</summary>
    public double P75Value { get; init; }
}

public sealed class ClientHistoryPointModel
{
    public string Hour { get; init; } = string.Empty;

    public long Errors { get; init; }

    public long Logs { get; init; }

    public long Events { get; init; }

    public long Vitals { get; init; }
}

public sealed class ClientEventPageModel
{
    public IReadOnlyList<ClientEventModel> Items { get; init; } = [];

    public int Total { get; init; }
}

public sealed class ClientEventModel
{
    public Guid Id { get; init; }

    public string? Application { get; init; }

    public ClientEventType Type { get; init; }

    public string? Name { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public double? Value { get; init; }

    public string? Url { get; init; }

    public Guid? TraceId { get; init; }

    public string? SessionId { get; init; }

    public DateTime Timestamp { get; init; }
}

public sealed class ClientEventDetailModel
{
    public Guid Id { get; init; }

    public string? Application { get; init; }

    public ClientEventType Type { get; init; }

    public string? Name { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public string? Stack { get; init; }

    public double? Value { get; init; }

    public string? Url { get; init; }

    public Guid? TraceId { get; init; }

    public string? SessionId { get; init; }

    public string? Release { get; init; }

    public string? UserAgent { get; init; }

    public string? RemoteIp { get; init; }

    public string? Properties { get; init; }

    public string? Breadcrumbs { get; init; }

    public DateTime Timestamp { get; init; }

    public DateTime ReceivedAt { get; init; }
}

public sealed class ClientSessionModel
{
    public string SessionId { get; init; } = string.Empty;

    public string? Application { get; init; }

    /// <summary>Client events and server endpoint calls for this session, merged and ordered by timestamp.</summary>
    public IReadOnlyList<ClientSessionEntryModel> Entries { get; init; } = [];
}

/// <summary>One row on the unified session timeline — either a client event (<c>Kind = "client"</c>) or a server endpoint call joined by trace id (<c>Kind = "endpoint"</c>).</summary>
public sealed class ClientSessionEntryModel
{
    public string Kind { get; init; } = string.Empty;

    public DateTime Timestamp { get; init; }

    public Guid? TraceId { get; init; }

    // Client-side fields (Kind == "client").
    public Guid? EventId { get; init; }

    public ClientEventType? Type { get; init; }

    public string? Name { get; init; }

    public string? Level { get; init; }

    public string? Message { get; init; }

    public double? Value { get; init; }

    public string? Url { get; init; }

    // Server-side fields (Kind == "endpoint").
    public string? Method { get; init; }

    public string? Route { get; init; }

    public int? StatusCode { get; init; }

    public double? DurationMs { get; init; }

    public string? Outcome { get; init; }
}

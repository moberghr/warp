using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Observability;

namespace Warp.Core.Endpoints;

/// <summary>
/// <see cref="IEndpointCallRecorder"/> that emits each completed inbound endpoint request as ONE
/// structured log via an injected <see cref="ILoggerFactory"/> (category <c>Warp.Endpoints.CallLog</c>)
/// instead of writing a database row — the inbound mirror of <c>OtelAdapterCallRecorder</c>. Every field
/// of the <see cref="EndpointCallRecord"/> is attached as a structured log property (via
/// <see cref="StructuredLogState"/>) so an OTLP logs exporter carries them as <c>LogRecord</c> attributes.
/// Selected by <c>RecordingSink.Otel</c>/<c>Both</c>. Public to mirror the public
/// <see cref="DbEndpointCallRecorder"/> so the <c>Warp.Http</c> binding can register it.
/// <para>
/// The fields are ALREADY redacted + truncated upstream by the capture pipeline (§1.2) — this recorder
/// re-emits them verbatim and adds no PII of its own. Level is <see cref="LogLevel.Warning"/> for a
/// failed outcome (status &gt;= 500 or an unhandled exception) and <see cref="LogLevel.Information"/>
/// otherwise. <see cref="Record"/> is non-blocking and never throws.
/// </para>
/// </summary>
public sealed class OtelEndpointCallRecorder : IEndpointCallRecorder
{
    /// <summary>Log category under which endpoint call records are emitted (each becomes an OTLP LogRecord).</summary>
    public const string LogCategory = "Warp.Endpoints.CallLog";

    private readonly ILogger _logger;
    private readonly WarpConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtelEndpointCallRecorder"/> class.
    /// </summary>
    public OtelEndpointCallRecorder(ILoggerFactory loggerFactory, IOptions<WarpConfiguration> configuration)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = loggerFactory.CreateLogger(LogCategory);
        _configuration = configuration.Value;
    }

    /// <inheritdoc />
    public bool Record(EndpointCallRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            var level = record.Outcome == AdapterCallOutcome.Failed ? LogLevel.Warning : LogLevel.Information;
            if (!_logger.IsEnabled(level))
            {
                return true;
            }

            var state = BuildState(record);
            _logger.Log(level, default, state, exception: null, StructuredLogState.Format);

            return true;
        }
#pragma warning disable CA1031 // Recording must never throw or fail a request — swallow any logging failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    private StructuredLogState BuildState(EndpointCallRecord record)
    {
        var outcome = EndpointCounterKeys.OutcomeToken(record.Outcome);

        var fields = new List<KeyValuePair<string, object?>>
        {
            new("method", record.Method),
            new("route", record.RouteTemplate),
            new("operation", record.Operation),
            new("outcome", outcome),
            new("durationMs", record.DurationMs),
            new("timestamp", record.Timestamp),
            new("machineName", record.MachineName),
        };

        AddIfNotNull(fields, "group", record.GroupName);
        AddIfNotNull(fields, "status", record.StatusCode);
        AddIfNotNull(fields, "remoteIp", record.RemoteIp);
        AddIfNotNull(fields, "userAgent", record.UserAgent);
        AddIfNotNull(fields, "user", record.User);
        AddIfNotNull(fields, "exceptionType", record.ExceptionType);
        AddIfNotNull(fields, "exceptionMessage", record.ExceptionMessage);
        AddIfNotNull(fields, "requestHeaders", record.RequestHeaders);
        AddIfNotNull(fields, "responseHeaders", record.ResponseHeaders);
        AddIfNotNull(fields, "requestBody", record.RequestBody);
        AddIfNotNull(fields, "responseBody", record.ResponseBody);
        AddIfNotNull(fields, "traceId", record.TraceId);
        AddIfNotNull(fields, "tags", record.TagsJson);
        AddIfNotNull(fields, "application", _configuration.ApplicationName);

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Endpoint call {record.Method} {record.RouteTemplate} -> {record.StatusCode} ({record.DurationMs}ms)");

        return new StructuredLogState(message, fields);
    }

    private static void AddIfNotNull(List<KeyValuePair<string, object?>> fields, string name, object? value)
    {
        if (value is not null)
        {
            fields.Add(new KeyValuePair<string, object?>(name, value));
        }
    }
}

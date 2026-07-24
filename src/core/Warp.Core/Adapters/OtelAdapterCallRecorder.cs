using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Enums;
using Warp.Core.Observability;

namespace Warp.Core.Adapters;

/// <summary>
/// <see cref="IAdapterCallRecorder"/> that emits each completed adapter call as ONE structured log via
/// an injected <see cref="ILoggerFactory"/> (category <c>Warp.Adapters.CallLog</c>) instead of writing a
/// database row. Every field of the <see cref="AdapterCallRecord"/> is attached as a structured log
/// property (via <see cref="StructuredLogState"/>) so an OTLP logs exporter carries them as
/// <c>LogRecord</c> attributes for an external collector. Selected by <c>RecordingSink.Otel</c>/<c>Both</c>.
/// <para>
/// The fields are ALREADY redacted + truncated upstream by the capture pipeline (§1.2) — this recorder
/// re-emits them verbatim and adds no PII of its own. Level is <see cref="LogLevel.Information"/> for a
/// successful outcome and <see cref="LogLevel.Warning"/> otherwise (failed / throttled / circuit-open).
/// <see cref="Record"/> is non-blocking (a single synchronous log write) and never throws — any logging
/// failure is swallowed and reported as accepted, mirroring the lossy-by-design recorder contract.
/// </para>
/// </summary>
internal sealed class OtelAdapterCallRecorder : IAdapterCallRecorder
{
    /// <summary>Log category under which adapter call records are emitted (each becomes an OTLP LogRecord).</summary>
    internal const string LogCategory = "Warp.Adapters.CallLog";

    private readonly ILogger _logger;
    private readonly WarpConfiguration _configuration;

    public OtelAdapterCallRecorder(ILoggerFactory loggerFactory, IOptions<WarpConfiguration> configuration)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(LogCategory);
        _configuration = configuration.Value;
    }

    public bool Record(AdapterCallRecord record)
    {
        try
        {
            var level = record.Outcome == AdapterCallOutcome.Success ? LogLevel.Information : LogLevel.Warning;
            if (!_logger.IsEnabled(level))
            {
                return true;
            }

            var state = BuildState(record);
            _logger.Log(level, default, state, exception: null, StructuredLogState.Format);

            return true;
        }
#pragma warning disable CA1031 // Recording must never throw or fail a user call — swallow any logging failure.
        catch (Exception)
#pragma warning restore CA1031
        {
            return true;
        }
    }

    private StructuredLogState BuildState(AdapterCallRecord record)
    {
        var outcome = AdapterCounterKeys.OutcomeToken(record.Outcome);

        // Required fields are always present; optional capture fields are added only when non-null so the
        // OTLP LogRecord carries an attribute exactly when the value was captured upstream.
        var fields = new List<KeyValuePair<string, object?>>
        {
            new("adapter", record.AdapterName),
            new("operation", record.Operation),
            new("outcome", outcome),
            new("durationMs", record.DurationMs),
            new("attempts", record.Attempts),
            new("timestamp", record.Timestamp),
            new("machineName", record.MachineName),
        };

        AddIfNotNull(fields, "group", record.GroupName);
        AddIfNotNull(fields, "status", record.StatusCode);
        AddIfNotNull(fields, "exceptionType", record.ExceptionType);
        AddIfNotNull(fields, "exceptionMessage", record.ExceptionMessage);
        AddIfNotNull(fields, "requestSummary", record.RequestSummary);
        AddIfNotNull(fields, "requestHeaders", record.RequestHeaders);
        AddIfNotNull(fields, "responseHeaders", record.ResponseHeaders);
        AddIfNotNull(fields, "requestBody", record.RequestBody);
        AddIfNotNull(fields, "responseBody", record.ResponseBody);
        AddIfNotNull(fields, "traceId", record.TraceId);
        AddIfNotNull(fields, "correlationId", record.CorrelationId);
        AddIfNotNull(fields, "application", _configuration.ApplicationName);

        if (record.Tags is not null)
        {
            foreach (var tag in record.Tags)
            {
                fields.Add(new KeyValuePair<string, object?>($"tag.{tag.Key}", tag.Value));
            }
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"Adapter call {record.AdapterName}.{record.Operation} -> {outcome} ({record.DurationMs}ms)");

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

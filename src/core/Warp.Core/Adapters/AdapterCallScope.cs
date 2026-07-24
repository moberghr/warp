using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Warp.Core.Enums;
using Warp.Core.Logging;

namespace Warp.Core.Adapters;

/// <summary>
/// A single outbound adapter call in flight. Obtained from <see cref="IWarpAdapters.BeginCall"/>;
/// the caller signals the outcome with <see cref="Succeed"/> / <see cref="Fail"/> (explicit is
/// encouraged) and may enrich the record with <see cref="SetGroup"/>, <see cref="SetCorrelation"/>,
/// <see cref="SetTag"/>, and the capture setters (<see cref="SetRequestSummary"/>,
/// <see cref="SetStatusCode"/>, <see cref="SetRequestHeaders"/>, <see cref="SetResponseHeaders"/>,
/// <see cref="SetRequestBody"/>, <see cref="SetResponseBody"/> — values arrive already redacted and
/// truncated by the transport binding) before completion. On completion the scope emits an OTel Client span and
/// the <c>warp.adapter.*</c> meters unconditionally, then hands a record to the (opt-in) recorder.
/// <para>
/// <b>Dispose semantics.</b> Disposing without an explicit outcome completes the call as
/// <c>Success</c>. Detecting an in-flight exception at dispose time is not possible without
/// <c>Marshal</c> (deliberately avoided), so call <see cref="Fail"/> explicitly on the failure path —
/// this is the reliable signal. Completion is idempotent: the first of Succeed/Fail/Dispose wins.
/// </para>
/// </summary>
public sealed class AdapterCallScope : IDisposable
{
    // Column caps mirror the AdapterCallLog schema (ServiceConfiguration.AddAdapterCallLogEntity). Clamp
    // every string field here — the single choke point — so an over-long caller value can never fail the
    // whole ≤500-record batch insert in the flusher. Because the counter keys are built from
    // AdapterName / Operation / GroupName (each ≤ NameCap), clamping also keeps every counter key inside
    // the Statistic/Counter 450-char key PK cap (worst case `adapter:{200}:grp:{200}:{outcome}` = 426).
    private const int NameCap = 200;
    private const int RequestSummaryCap = 2048;
    private const int ExceptionTypeCap = 512;
    private const int MachineNameCap = 256;
    private const int TraceIdCap = 64;

    private readonly string _adapter;
    private readonly string _operation;
    private readonly Func<string, string> _mapGroup;
    private readonly WarpAdapterOptions _options;
    private readonly IAdapterCallRecorder _recorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly Activity? _activity;
    private readonly long _startTimestamp;
    private readonly DateTime _startedAt;
    private readonly int _attempts;

    private Dictionary<string, string>? _tags;
    private string? _group;
    private string? _correlationId;
    private string? _requestSummary;
    private int? _statusCode;
    private string? _requestHeaders;
    private string? _responseHeaders;
    private string? _requestBody;
    private string? _responseBody;
    private bool _forceCapture;
    private int _completed;

    internal AdapterCallScope(
        string adapter,
        string operation,
        string? group,
        WarpAdapterOptions options,
        Func<string, string> mapGroup,
        IAdapterCallRecorder recorder,
        TimeProvider timeProvider,
        ILogger logger,
        Activity? activity)
    {
        _adapter = adapter;
        _operation = operation;
        _group = group;
        _options = options;
        _mapGroup = mapGroup;
        _recorder = recorder;
        _timeProvider = timeProvider;
        _logger = logger;
        _activity = activity;
        _attempts = 1;
        _startTimestamp = timeProvider.GetTimestamp();
        _startedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    public void Succeed() => Complete(AdapterCallOutcome.Success, null);

    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var outcome = exception is AdapterRateLimitedException
            ? AdapterCallOutcome.Throttled
            : AdapterCallOutcome.Failed;

        Complete(outcome, exception);
    }

    public void SetGroup(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        _group = _mapGroup(group);
    }

    public void SetCorrelation(string correlationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        _correlationId = correlationId;
    }

    public void SetTag(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        _tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
        _tags[key] = value;
    }

    /// <summary>
    /// Sets the non-secret request summary line (e.g. <c>GET /orders/{id}</c>) stored in the
    /// <c>RequestSummary</c> capture column. Metadata, not a payload — safe to set even when body
    /// capture is off.
    /// </summary>
    public void SetRequestSummary(string requestSummary)
    {
        ArgumentNullException.ThrowIfNull(requestSummary);

        _requestSummary = requestSummary;
    }

    /// <summary>Sets the response status code stored in the <c>StatusCode</c> capture column.</summary>
    public void SetStatusCode(int statusCode) => _statusCode = statusCode;

    /// <summary>
    /// Sets the captured request headers (already redacted and truncated by the caller) stored in the
    /// <c>RequestHeaders</c> capture column.
    /// </summary>
    public void SetRequestHeaders(string requestHeaders)
    {
        ArgumentNullException.ThrowIfNull(requestHeaders);

        _requestHeaders = requestHeaders;
    }

    /// <summary>
    /// Sets the captured response headers (already redacted and truncated by the caller) stored in the
    /// <c>ResponseHeaders</c> capture column.
    /// </summary>
    public void SetResponseHeaders(string responseHeaders)
    {
        ArgumentNullException.ThrowIfNull(responseHeaders);

        _responseHeaders = responseHeaders;
    }

    /// <summary>
    /// Sets the captured request body (already truncated by the caller) stored in the
    /// <c>RequestBody</c> capture column.
    /// </summary>
    public void SetRequestBody(string requestBody)
    {
        ArgumentNullException.ThrowIfNull(requestBody);

        _requestBody = requestBody;
    }

    /// <summary>
    /// Sets the captured response body (already truncated by the caller) stored in the
    /// <c>ResponseBody</c> capture column.
    /// </summary>
    public void SetResponseBody(string responseBody)
    {
        ArgumentNullException.ThrowIfNull(responseBody);

        _responseBody = responseBody;
    }

    /// <summary>
    /// Forces this call's log row to be written and its capture tiers to full fidelity regardless of the
    /// adapter's <c>SampleRate</c> / <c>RecordCalls</c> — the transport binding sets it from the per-call
    /// force-capture request option or ambient scope. Must be set before completion. Counters/telemetry are
    /// unaffected (they always record).
    /// </summary>
    public void SetForceCapture(bool forceCapture) => _forceCapture = forceCapture;

    public void Dispose() => Complete(AdapterCallOutcome.Success, null);

    private void Complete(AdapterCallOutcome outcome, Exception? exception)
    {
        // First of Succeed/Fail/Dispose wins; the rest are no-ops.
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        var duration = _timeProvider.GetElapsedTime(_startTimestamp).TotalMilliseconds;

        if (_options.EnrichCall is not null)
        {
            try
            {
                _options.EnrichCall(this);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adapter {Adapter} EnrichCall threw; continuing without enrichment.", _adapter);
            }
        }

        // Telemetry + recording are best-effort observation of a call that already happened: a throwing
        // ActivityListener/MeterListener callback or a throwing recorder must never propagate out of
        // Complete. The handler calls Fail(ex) inside its catch block, so an unguarded throw here would
        // replace the caller's real transport exception with a recording exception. Same LogWarning
        // policy as EnrichCall above. Telemetry is guarded SEPARATELY so a throwing meter/activity listener
        // does not skip RecordCall — the call record must still land when only telemetry fails.
        try
        {
            try
            {
                EmitTelemetry(outcome, exception, duration);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adapter {Adapter} telemetry threw; call still recorded.", _adapter);
            }

            RecordCall(outcome, exception, duration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adapter {Adapter} recording threw; call outcome preserved.", _adapter);
        }
        finally
        {
            _activity?.Dispose();
        }
    }

    private void EmitTelemetry(AdapterCallOutcome outcome, Exception? exception, double duration)
    {
        var outcomeName = outcome.ToString();

        if (_activity is not null)
        {
            _activity.SetTag(WarpTelemetryAttributes.WarpAdapterOutcome, outcomeName);

            if (_group is not null)
            {
                _activity.SetTag(WarpTelemetryAttributes.WarpAdapterGroup, _group);
            }

            if (exception is not null)
            {
                _activity.SetTag(WarpTelemetryAttributes.ErrorType, exception.GetType().FullName);
                _activity.SetStatus(ActivityStatusCode.Error, WarpTelemetry.TruncateMessage(exception.Message, 256));
            }

            _activity.Stop();
        }

        var tags = new TagList
        {
            { WarpTelemetryAttributes.AdapterMeterAdapter, _adapter },
            { WarpTelemetryAttributes.AdapterMeterOperation, _operation },
            { WarpTelemetryAttributes.AdapterMeterOutcome, outcomeName },
        };

        // Process origin (WarpConfiguration.ApplicationName) — a low-cardinality identity, added to the meter
        // tags when set so an OTel user can slice adapter metrics per application (mirrors the per-app DB
        // Counter dimension). Null (feature off) ⇒ no tag. Group stays gated behind IncludeGroupInMetrics.
        if (WarpTelemetry.ApplicationName is not null)
        {
            tags.Add(WarpTelemetryAttributes.MeterApplication, WarpTelemetry.ApplicationName);
        }

        if (_options.IncludeGroupInMetrics && _group is not null)
        {
            tags.Add(WarpTelemetryAttributes.AdapterMeterGroup, _group);
        }

        WarpTelemetry.AdapterCalls.Add(1, tags);
        WarpTelemetry.AdapterDuration.Record(duration, tags);
    }

    private void RecordCall(AdapterCallOutcome outcome, Exception? exception, double duration)
    {
        // Volume controls suppress the call-log ROW only — counters, the LastSeenAt/definition upsert, and
        // telemetry are unaffected (successes are always counted so ErrorRate has a real denominator). Always
        // hand the record over; the flusher honours SuppressLog by skipping the AdapterCallLog row. A row is
        // written for any failure, any forced call, and successes kept by both FailuresOnly-vs-All and the
        // sample rate; it is suppressed only for a non-forced success that either mode dropped.
        var failure = outcome != AdapterCallOutcome.Success;

#pragma warning disable CA5394 // Sampling is a volume knob, not a security decision — non-crypto RNG is fine.
        var sampledIn = _options.SampleRate >= 1.0 || Random.Shared.NextDouble() < _options.SampleRate;
#pragma warning restore CA5394

        var suppressLog = !failure
            && !_forceCapture
            && (_options.RecordCalls == CallRecording.FailuresOnly || !sampledIn);

        var record = new AdapterCallRecord
        {
            AdapterName = Clamp(_adapter, NameCap)!,
            Operation = Clamp(_operation, NameCap)!,
            GroupName = Clamp(_group, NameCap),
            Timestamp = _startedAt,
            DurationMs = duration,
            Attempts = _attempts,
            Outcome = outcome,
            StatusCode = _statusCode,
            ExceptionType = Clamp(exception?.GetType().FullName, ExceptionTypeCap),
            ExceptionMessage = exception is null ? null : WarpTelemetry.TruncateMessage(exception.Message, 4096),
            RequestSummary = Clamp(_requestSummary, RequestSummaryCap),
            RequestHeaders = _requestHeaders,
            ResponseHeaders = _responseHeaders,
            RequestBody = _requestBody,
            ResponseBody = _responseBody,
            MachineName = Clamp(Environment.MachineName, MachineNameCap)!,
            TraceId = Clamp((_activity?.TraceId ?? Activity.Current?.TraceId)?.ToHexString(), TraceIdCap),
            Tags = _tags?.ToArray(),
            CorrelationId = Clamp(_correlationId, NameCap),
            SuppressLog = suppressLog,
        };

        if (!_recorder.Record(record))
        {
            WarpTelemetry.AdapterRecordsDropped.Add(
                1,
                new KeyValuePair<string, object?>(WarpTelemetryAttributes.AdapterMeterAdapter, _adapter));
        }
    }

    private static string? Clamp(string? value, int max)
        => value is not null && value.Length > max ? value[..max] : value;
}

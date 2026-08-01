using Warp.Core.Data.Entities;
using Warp.Core.Enums;

namespace Warp.Core.ErrorGrouping;

/// <summary>
/// Builds <see cref="ErrorOccurrence"/> inbox rows for the four error sources (§8.29), with the column-length
/// truncation centralized so every append point stays within the schema. The caller adds the row to its own
/// context (jobs in the finalization save; the endpoint middleware / adapter+client flushers off the hot path).
/// </summary>
public static class ErrorOccurrenceFactory
{
    private const int TypeMax = 512;
    private const int MessageMax = 4096;
    private const int CulpritMax = 512;
    private const int StackMax = 8192;

    /// <summary>
    /// From a live <see cref="Exception"/> (the job worker path). Groups on the REAL cause: reflection-invoked
    /// handlers (and aggregate/collate paths) wrap the true exception, so the identity type + message come from
    /// the unwrapped inner exception — otherwise every handler error masquerades as one
    /// <c>TargetInvocationException</c> issue. The full wrapper <see cref="Exception.ToString"/> is kept as the
    /// sample stack (it contains the inner exception and the real handler frame, which the fingerprint's top
    /// in-app frame is extracted from).
    /// </summary>
    public static ErrorOccurrence FromException(ErrorSource source, Exception error, string culprit, Guid? traceId, string? application, DateTime timestamp, string? version = null, string? environment = null)
    {
        var cause = Unwrap(error);

        return FromError(source, cause.GetType().FullName ?? cause.GetType().Name, cause.Message, error.ToString(), culprit, traceId, application, timestamp, version, environment);
    }

    private static Exception Unwrap(Exception error)
    {
        var current = error;
        while (current is System.Reflection.TargetInvocationException or AggregateException && current.InnerException is { } inner)
        {
            current = inner;
        }

        return current;
    }

    /// <summary>From already-captured strings (endpoint 5xx / adapter / client, where the row holds type+message+stack).</summary>
    public static ErrorOccurrence FromError(ErrorSource source, string? exceptionType, string? message, string? stack, string culprit, Guid? traceId, string? application, DateTime timestamp, string? version = null, string? environment = null)
        => new()
        {
            Source = source,
            Kind = ErrorKind.Exception,
            ExceptionType = Cap(exceptionType, TypeMax) ?? "Error",
            Message = Cap(message, MessageMax),
            Stack = Cap(stack, StackMax),
            Culprit = Cap(culprit, CulpritMax) ?? string.Empty,
            TraceId = traceId,
            Application = application,
            Version = version,
            Environment = environment,
            Timestamp = timestamp,
        };

    /// <summary>An endpoint 4xx status-code signal — no exception, grouped by status + route.</summary>
    public static ErrorOccurrence FromStatusCode(int statusCode, string route, Guid? traceId, string? application, DateTime timestamp, string? version = null, string? environment = null)
        => new()
        {
            Source = ErrorSource.Endpoint,
            Kind = ErrorKind.StatusCode,
            ExceptionType = $"HTTP {statusCode}",
            StatusCode = statusCode,
            Culprit = Cap(route, CulpritMax) ?? string.Empty,
            TraceId = traceId,
            Application = application,
            Version = version,
            Environment = environment,
            Timestamp = timestamp,
        };

    private static string? Cap(string? value, int max)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= max ? value : value[..max];
    }
}

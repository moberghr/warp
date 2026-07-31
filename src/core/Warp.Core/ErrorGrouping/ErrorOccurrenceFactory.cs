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

    /// <summary>From a live <see cref="Exception"/> (the job worker path).</summary>
    public static ErrorOccurrence FromException(ErrorSource source, Exception error, string culprit, Guid? traceId, string? application, DateTime timestamp)
        => FromError(source, error.GetType().FullName ?? error.GetType().Name, error.Message, error.ToString(), culprit, traceId, application, timestamp);

    /// <summary>From already-captured strings (endpoint 5xx / adapter / client, where the row holds type+message+stack).</summary>
    public static ErrorOccurrence FromError(ErrorSource source, string? exceptionType, string? message, string? stack, string culprit, Guid? traceId, string? application, DateTime timestamp)
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
            Timestamp = timestamp,
        };

    /// <summary>An endpoint 4xx status-code signal — no exception, grouped by status + route.</summary>
    public static ErrorOccurrence FromStatusCode(int statusCode, string route, Guid? traceId, string? application, DateTime timestamp)
        => new()
        {
            Source = ErrorSource.Endpoint,
            Kind = ErrorKind.StatusCode,
            ExceptionType = $"HTTP {statusCode}",
            StatusCode = statusCode,
            Culprit = Cap(route, CulpritMax) ?? string.Empty,
            TraceId = traceId,
            Application = application,
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

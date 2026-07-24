using System.Collections;

namespace Warp.Core.Observability;

/// <summary>
/// Log state carried by the OTel call-log recorders. Holds the completed record's fields as an ordered
/// <see cref="KeyValuePair{TKey,TValue}"/> list so an OTLP logs exporter surfaces each one as a
/// <c>LogRecord</c> attribute, plus a precomputed one-line message. Passed as the <c>TState</c> to
/// <see cref="Microsoft.Extensions.Logging.ILogger.Log{TState}"/> with <see cref="Format"/> as the
/// formatter — no message template, so there is no interpolation cost and no CA2254.
/// </summary>
internal sealed class StructuredLogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly IReadOnlyList<KeyValuePair<string, object?>> _fields;
    private readonly string _message;

    public StructuredLogState(string message, IReadOnlyList<KeyValuePair<string, object?>> fields)
    {
        _message = message;
        _fields = fields;
    }

    public int Count => _fields.Count;

    public KeyValuePair<string, object?> this[int index] => _fields[index];

    /// <summary>Formatter for <c>ILogger.Log</c> — returns the precomputed message, ignoring the exception.</summary>
    public static string Format(StructuredLogState state, Exception? exception) => state._message;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => _message;
}

namespace Warp.Adapters.Http;

/// <summary>
/// Ambient <see cref="AsyncLocal{T}"/> naming scopes for outbound adapter calls. Use when the
/// operation (or group) is known at the call site but the <see cref="HttpRequestMessage"/> is not
/// conveniently reachable — e.g. a shared SOAP/GraphQL transport method that posts to one URL:
/// <code>
/// using (WarpAdapterCall.Operation("payment.capture"))
/// {
///     await _client.PostAsync(uri, content);
/// }
/// </code>
/// The value flows to every outbound call made on the same async context while the scope is open.
/// Precedence: an <c>HttpRequestMessage</c> option (<see cref="HttpRequestMessageExtensions.WithWarpOperation"/>)
/// wins over the ambient scope, which in turn wins over the URL heuristic.
/// <para>
/// <b>Threading caveat.</b> <see cref="AsyncLocal{T}"/> does not flow across manually created threads
/// (<c>new Thread</c>, unawaited <c>Task.Run</c> without capture). The request option is the reliable
/// path for those cases.
/// </para>
/// </summary>
public static class WarpAdapterCall
{
    private static readonly AsyncLocal<string?> _operation = new();
    private static readonly AsyncLocal<string?> _group = new();
    private static readonly AsyncLocal<bool> _forceCapture = new();

    internal static string? CurrentOperation => _operation.Value;

    internal static string? CurrentGroup => _group.Value;

    internal static bool CurrentForceCapture => _forceCapture.Value;

    /// <summary>
    /// Pushes an ambient operation name for the current async context. Dispose to restore the previous
    /// value (scopes nest).
    /// </summary>
    public static IDisposable Operation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var previous = _operation.Value;
        _operation.Value = operation;

        return new Popper(() => _operation.Value = previous);
    }

    /// <summary>
    /// Pushes an ambient group (runtime who/where — endpoint, tenant, shop) for the current async
    /// context. Dispose to restore the previous value (scopes nest).
    /// </summary>
    public static IDisposable Group(string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        var previous = _group.Value;
        _group.Value = group;

        return new Popper(() => _group.Value = previous);
    }

    /// <summary>
    /// Forces full-fidelity capture (request + response bodies and headers, even on success and even if the
    /// capture tier is <c>None</c>/<c>OnFailure</c>) and always writes the call-log row for every outbound
    /// call made on the current async context — bypassing the adapter's <c>SampleRate</c> and
    /// <c>RecordCalls</c>. Dispose to restore the previous value (scopes nest). ORs with the per-request
    /// <see cref="HttpRequestMessageExtensions.WithWarpForceCapture"/> option. PII-owned (§1.2).
    /// </summary>
    public static IDisposable ForceCapture()
    {
        var previous = _forceCapture.Value;
        _forceCapture.Value = true;

        return new Popper(() => _forceCapture.Value = previous);
    }

    private sealed class Popper : IDisposable
    {
        private Action? _restore;

        public Popper(Action restore) => _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }
}

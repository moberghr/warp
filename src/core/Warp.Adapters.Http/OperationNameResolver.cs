using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Warp.Adapters.Http;

/// <summary>
/// Resolves the operation name for an outbound HTTP request, applying the fixed precedence
/// <c>request option &gt; ambient scope &gt; URL heuristic</c> (SC2). The heuristic collapses numeric and
/// GUID path segments to <c>{id}</c> so <c>GET /orders/123</c> and <c>GET /orders/456</c> share one
/// operation. Heuristic-derived names are bounded per adapter by <c>MaxDistinctOperations</c> (SC12):
/// beyond the cap, further new heuristic names collapse to the literal <c>{other}</c> with a one-time
/// warning. Explicitly-supplied names (option/ambient) are authoritative and never collapsed.
/// Registered as a singleton so the per-adapter distinct-name state survives across requests.
/// </summary>
internal sealed class OperationNameResolver
{
    internal const string OtherOperation = "{other}";
    internal const string IdPlaceholder = "{id}";

    private readonly ILogger<OperationNameResolver> _logger;
    private readonly ConcurrentDictionary<string, HeuristicGuard> _guards = new(StringComparer.OrdinalIgnoreCase);

    public OperationNameResolver(ILogger<OperationNameResolver> logger) => _logger = logger;

    public string Resolve(string adapterName, HttpRequestMessage request, int maxDistinctOperations)
    {
        var explicitName = request.GetWarpOperation() ?? WarpAdapterCall.CurrentOperation;
        if (explicitName is not null)
        {
            return explicitName;
        }

        var heuristic = Heuristic(request);
        var guard = _guards.GetOrAdd(adapterName, name => new HeuristicGuard(name, maxDistinctOperations, _logger));

        return guard.Map(heuristic);
    }

    public static string? ResolveGroup(HttpRequestMessage request)
        => request.GetWarpGroup() ?? WarpAdapterCall.CurrentGroup;

    internal static string Heuristic(HttpRequestMessage request)
    {
        var method = request.Method.Method;
        var path = ExtractPath(request.RequestUri);

        return $"{method} {CollapsePath(path)}";
    }

    private static string ExtractPath(Uri? uri)
    {
        if (uri is null)
        {
            return "/";
        }

        if (uri.IsAbsoluteUri)
        {
            return uri.AbsolutePath;
        }

        var original = uri.OriginalString;
        var queryStart = original.IndexOf('?', StringComparison.Ordinal);

        return queryStart >= 0 ? original[..queryStart] : original;
    }

    private static string CollapsePath(string path)
    {
        if (string.IsNullOrEmpty(path) || string.Equals(path, "/", StringComparison.Ordinal))
        {
            return "/";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (IsVariable(segments[i]))
            {
                segments[i] = IdPlaceholder;
            }
        }

        return "/" + string.Join('/', segments);
    }

    private static bool IsVariable(string segment)
    {
        if (Guid.TryParse(segment, out _))
        {
            return true;
        }

        return segment.Length > 0 && segment.All(char.IsDigit);
    }

    /// <summary>
    /// Bounded distinct-value guard for one adapter's heuristic-derived operation names. Mirrors the
    /// Core <c>CardinalityGuard</c> (which is internal to Warp.Core and therefore not reachable from this
    /// addon per §0.5); duplicated here rather than reaching across the assembly boundary.
    /// </summary>
    private sealed class HeuristicGuard
    {
        private readonly string _adapter;
        private readonly int _maxDistinct;
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();
        private int _warned;

        public HeuristicGuard(string adapter, int maxDistinct, ILogger logger)
        {
            _adapter = adapter;
            _maxDistinct = maxDistinct;
            _logger = logger;
        }

        public string Map(string value)
        {
            if (_seen.ContainsKey(value))
            {
                return value;
            }

            // Lock the count-then-add pair: a lock-free check-then-add lets concurrent NEW values all clear
            // the count check before any is added, overshooting the cap. Contention is low (only the first
            // sighting of each value contends), so a simple lock suffices.
            lock (_gate)
            {
                if (_seen.ContainsKey(value))
                {
                    return value;
                }

                if (_seen.Count >= _maxDistinct)
                {
                    WarnOnce();

                    return OtherOperation;
                }

                _seen.TryAdd(value, 0);

                return value;
            }
        }

        private void WarnOnce()
        {
            if (Interlocked.CompareExchange(ref _warned, 1, 0) == 0)
            {
                _logger.LogWarning(
                    "Adapter {Adapter} exceeded its heuristic operation cardinality cap of {Max}; further new URL-derived operations are recorded under \"{Other}\". Supply explicit names via WithWarpOperation to avoid collapsing.",
                    _adapter,
                    _maxDistinct,
                    OtherOperation);
            }
        }
    }
}

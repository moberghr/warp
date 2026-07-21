using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Warp.Core.Logging;

namespace Warp.Core.Adapters;

/// <summary>
/// Entry point for manual outbound-call observability. Wrap any outbound dependency call (SOAP proxy,
/// vendor SDK, non-HTTP transport) in a <see cref="BeginCall"/> scope to get identical telemetry,
/// capture, and dashboard treatment as the HTTP binding. Registered by <c>AddAdapters()</c>.
/// </summary>
public interface IWarpAdapters
{
    /// <summary>
    /// Begins a call scope for <paramref name="adapter"/> / <paramref name="operation"/>, optionally
    /// carrying a runtime <paramref name="group"/> (who/where — endpoint, tenant, shop). Returns a
    /// disposable scope; signal the outcome via <see cref="AdapterCallScope.Succeed"/> /
    /// <see cref="AdapterCallScope.Fail"/>.
    /// </summary>
    AdapterCallScope BeginCall(string adapter, string operation, string? group = null);
}

/// <summary>
/// Default <see cref="IWarpAdapters"/> implementation. Holds per-adapter runtime state (resolved
/// options + the group cardinality guard) and threads the recorder/time/log dependencies into each
/// scope. Registered as a singleton so cardinality state persists across calls.
/// </summary>
internal sealed class WarpAdapters : IWarpAdapters
{
    private readonly AdapterRegistry _registry;
    private readonly IAdapterCallRecorder _recorder;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WarpAdapters> _logger;

    // Ordinal (case-SENSITIVE) so in-memory per-adapter state agrees with the case-sensitive DB rows and
    // counter keys — case variants are independent adapters everywhere (see AdapterName.Validate).
    private readonly ConcurrentDictionary<string, AdapterState> _states = new(StringComparer.Ordinal);

    public WarpAdapters(
        AdapterRegistry registry,
        IAdapterCallRecorder recorder,
        TimeProvider timeProvider,
        ILogger<WarpAdapters> logger)
    {
        _registry = registry;
        _recorder = recorder;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public AdapterCallScope BeginCall(string adapter, string operation, string? group = null)
    {
        AdapterName.Validate(adapter, nameof(adapter));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        var state = _states.GetOrAdd(adapter, CreateState);
        var mappedGroup = group is null ? null : state.GroupGuard.Map(group);
        var activity = WarpTelemetry.StartAdapterActivity(adapter, operation);

        return new AdapterCallScope(
            adapter,
            operation,
            mappedGroup,
            state.Options,
            state.GroupGuard.Map,
            _recorder,
            _timeProvider,
            _logger,
            activity);
    }

    private AdapterState CreateState(string adapter)
    {
        var options = _registry.Resolve(adapter);
        var groupGuard = new CardinalityGuard(adapter, "group", options.MaxDistinctGroups, _logger);

        return new AdapterState(options, groupGuard);
    }

    private sealed class AdapterState
    {
        public AdapterState(WarpAdapterOptions options, CardinalityGuard groupGuard)
        {
            Options = options;
            GroupGuard = groupGuard;
        }

        public WarpAdapterOptions Options { get; }

        public CardinalityGuard GroupGuard { get; }
    }
}

/// <summary>
/// Holds the per-adapter <see cref="WarpAdapterOptions"/> registered at configuration time. Adapters
/// that were never explicitly registered (e.g. an ad-hoc manual scope) resolve to defaults. Singleton.
/// </summary>
internal sealed class AdapterRegistry
{
    private static readonly WarpAdapterOptions _default = new();

    // Ordinal (case-SENSITIVE): registry identity must agree with the case-sensitive DB rows / counter
    // keys so a case variant resolves to its own registration, not another adapter's (see AdapterName.Validate).
    private readonly ConcurrentDictionary<string, AdapterRegistration> _registrations = new(StringComparer.Ordinal);

    public AdapterRegistry()
    {
    }

    // DI-injected: every binding package registers its per-adapter AdapterRegistrationEntry as a singleton
    // at AddAdapter time; the singleton registry folds them in on first resolve (before any BeginCall /
    // flusher upsert). Keeps the registry internal — binding packages compose against the public entry
    // DTO only (§0.5), never reach into Core internals.
    public AdapterRegistry(IEnumerable<AdapterRegistrationEntry> entries)
    {
        foreach (var entry in entries)
        {
            Register(entry.Name, entry.Options, entry.ConfigSummary);
        }
    }

    public void Register(string name, WarpAdapterOptions options, string? configSummary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);

        _registrations[name] = new AdapterRegistration(options, configSummary);
    }

    public WarpAdapterOptions Resolve(string name)
        => _registrations.TryGetValue(name, out var registration) ? registration.Options : _default;

    public string? ResolveConfigSummary(string name)
        => _registrations.TryGetValue(name, out var registration) ? registration.ConfigSummary : null;

    // Null for adapters that were never explicitly registered (ad-hoc manual scopes) — the definition's
    // GroupLabel then falls back to "Group" at read time, matching the WarpAdapterOptions default.
    public string? ResolveGroupLabel(string name)
        => _registrations.TryGetValue(name, out var registration) ? registration.Options.GroupLabel : null;

    private sealed record AdapterRegistration(WarpAdapterOptions Options, string? ConfigSummary);
}

/// <summary>
/// The per-adapter recording configuration a binding package (<c>Warp.Adapters.Http</c> / <c>.Refit</c>)
/// contributes at <c>AddAdapter</c> time. Registered as a DI singleton so the <c>AdapterRegistry</c>
/// singleton folds every entry in on first resolve — the DI-correct seam that keeps the registry
/// internal while binding packages compose against Core's public API only (§0.5). <see cref="ConfigSummary"/>
/// is a non-secret display string (capture modes, resilience on/off, shared-limit) surfaced on the
/// dashboard via <c>AdapterDefinition.ConfigSummary</c>.
/// </summary>
public sealed record AdapterRegistrationEntry(string Name, WarpAdapterOptions Options, string? ConfigSummary);

/// <summary>
/// Bounded distinct-value guard for a single adapter dimension (operations or groups). The first
/// <c>maxDistinct</c> distinct values pass through unchanged; every further new value collapses to the
/// literal <see cref="OtherValue"/> with a one-time warning, protecting counter/metric cardinality
/// from fan-out adapters. Reused by the HTTP operation-name resolver for the operations dimension.
/// </summary>
internal sealed class CardinalityGuard
{
    internal const string OtherValue = "{other}";

    private readonly string _adapter;
    private readonly string _dimension;
    private readonly int _maxDistinct;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private int _warned;

    public CardinalityGuard(string adapter, string dimension, int maxDistinct, ILogger logger)
    {
        _adapter = adapter;
        _dimension = dimension;
        _maxDistinct = maxDistinct;
        _logger = logger;
    }

    public string Map(string value)
    {
        if (_seen.ContainsKey(value))
        {
            return value;
        }

        // Lock the count-then-add pair: a lock-free check-then-add lets N concurrent NEW values all pass
        // the count check before any is added, overshooting the cap. Contention is low (per adapter, only
        // the first sighting of each value contends), so a simple lock is fine.
        lock (_gate)
        {
            if (_seen.ContainsKey(value))
            {
                return value;
            }

            if (_seen.Count >= _maxDistinct)
            {
                WarnOnce();

                return OtherValue;
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
                "Adapter {Adapter} exceeded its {Dimension} cardinality cap of {Max}; further new values are recorded under \"{Other}\".",
                _adapter,
                _dimension,
                _maxDistinct,
                OtherValue);
        }
    }
}

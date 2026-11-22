using System.Reflection;
using Microsoft.AspNetCore.Builder;

namespace Warp.Tests.Helpers;

/// <summary>
/// A class fixture that starts each distinct test host once per class instead of once per test.
/// <para>
/// Starting a host that maps the dashboard builds every one of its endpoints, measured at one to three
/// seconds per start on a developer machine, and <c>DashboardAuthTests</c> started 37 of them for 42
/// tests, most of them the same three configurations. A host is identified by the methods that configure it, so two tests that pass
/// the same named methods share one. A lambda that captures a variable is never shared, because two
/// captures of one lambda are one method with different state.
/// </para>
/// <para>
/// Only for hosts whose state a test cannot change: these serve requests, and anything a test varies
/// (cookies, headers) lives on its own <c>HttpClient</c>.
/// </para>
/// </summary>
public sealed class SharedTestHosts : IAsyncLifetime
{
    private readonly Dictionary<HostKey, WebApplication> _hosts = [];
    private readonly List<WebApplication> _unshared = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.Values.Concat(_unshared))
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The host configured by <paramref name="configuredBy"/> (and <paramref name="variant"/>, for what
    /// is not a delegate, such as a route prefix), started on first use.
    /// </summary>
    public async Task<WebApplication> GetOrStartAsync(IReadOnlyList<Delegate?> configuredBy, string variant, Func<Task<WebApplication>> start)
    {
        if (configuredBy.Any(x => x != null && CapturesState(x)))
        {
            var own = await start();
            _unshared.Add(own);

            return own;
        }

        var key = new HostKey([.. configuredBy.Select(x => x?.Method)], variant);
        if (!_hosts.TryGetValue(key, out var host))
        {
            host = await start();
            _hosts[key] = host;
        }

        return host;
    }

    // A static method or a lambda that captures nothing: the compiler keeps the latter on a shared
    // instance of a class with no fields. A capturing lambda lives on a class that has them.
    private static bool CapturesState(Delegate configure) =>
        configure.Target is { } target
        && target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0;

    private sealed class HostKey : IEquatable<HostKey>
    {
        private readonly MethodInfo?[] _methods;
        private readonly string _variant;

        public HostKey(MethodInfo?[] methods, string variant)
        {
            _methods = methods;
            _variant = variant;
        }

        public bool Equals(HostKey? other) =>
            other != null
            && string.Equals(_variant, other._variant, StringComparison.Ordinal)
            && _methods.SequenceEqual(other._methods);

        public override bool Equals(object? obj) => Equals(obj as HostKey);

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            hash.Add(_variant, StringComparer.Ordinal);
            foreach (var method in _methods)
            {
                hash.Add(method);
            }

            return hash.ToHashCode();
        }
    }
}

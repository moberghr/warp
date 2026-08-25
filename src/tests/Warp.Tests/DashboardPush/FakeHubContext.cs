using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Warp.Dashboard.Push;

namespace Warp.Tests.DashboardPush;

/// <summary>
/// Test double for <see cref="IHubContext{THub}"/> that captures every <c>SendAsync</c> /
/// <c>SendCoreAsync</c> call to <c>Clients.All</c> into a thread-safe queue. Used across the
/// broadcaster unit tests and the integration tests as the assertion surface.
/// <para>
/// <see cref="WaitForBroadcastsAsync"/> and <see cref="WaitForMethodAsync"/> let tests block
/// deterministically until a broadcast lands — each enqueue wakes any matching subscription
/// the same instant <c>SendCoreAsync</c> returns, instead of polling at a fixed interval.
/// </para>
/// </summary>
public sealed class FakeHubContext : IHubContext<WarpDashboardHub>
{
    private readonly FakeHubClients _clients;
    private readonly List<Subscription> _subscriptions = [];
    private readonly Lock _subscriptionsLock = new();

    public FakeHubContext()
    {
        _clients = new FakeHubClients(NotifySubscribers);
    }

    public IHubClients Clients => _clients;

    public IGroupManager Groups { get; } = new NotSupportedGroupManager();

    public IReadOnlyCollection<(string Method, object?[] Args)> Broadcasts => _clients.AllProxy.Broadcasts;

    public int CountOf(string method) => Broadcasts.Count(x => string.Equals(x.Method, method, StringComparison.Ordinal));

    /// <summary>
    /// Awaits until <paramref name="expectedAtLeast"/> total broadcasts have been observed.
    /// Returns immediately if the threshold is already met.
    /// </summary>
    public async Task WaitForBroadcastsAsync(int expectedAtLeast, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);

        var subscription = Subscribe(_ => Broadcasts.Count >= expectedAtLeast);

        try
        {
            if (Broadcasts.Count >= expectedAtLeast)
            {
                return;
            }

            await subscription.Task.WaitAsync(effectiveTimeout, Xunit.TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"Expected at least {expectedAtLeast} broadcasts within {effectiveTimeout}, observed {Broadcasts.Count}.");
        }
        finally
        {
            Unsubscribe(subscription);
        }
    }

    /// <summary>
    /// Awaits until at least one broadcast with <paramref name="method"/> has been observed.
    /// </summary>
    public async Task WaitForMethodAsync(string method, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(5);

        var subscription = Subscribe(b => string.Equals(b.Method, method, StringComparison.Ordinal));

        try
        {
            if (CountOf(method) > 0)
            {
                return;
            }

            await subscription.Task.WaitAsync(effectiveTimeout, Xunit.TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            var observed = string.Join(", ", Broadcasts.Select(x => x.Method));

            throw new TimeoutException(
                $"FakeHubContext did not observe '{method}' broadcast within {effectiveTimeout}. " +
                $"Observed methods: [{observed}]");
        }
        finally
        {
            Unsubscribe(subscription);
        }
    }

    private Subscription Subscribe(Func<(string Method, object?[] Args), bool> match)
    {
        var subscription = new Subscription(match);

        lock (_subscriptionsLock)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_subscriptionsLock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private void NotifySubscribers((string Method, object?[] Args) broadcast)
    {
        Subscription[] toComplete;

        lock (_subscriptionsLock)
        {
            toComplete = [.. _subscriptions.Where(s => s.Matches(broadcast))];
        }

        foreach (var subscription in toComplete)
        {
            subscription.Complete();
        }
    }

    private sealed class Subscription
    {
        private readonly Func<(string Method, object?[] Args), bool> _match;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Subscription(Func<(string Method, object?[] Args), bool> match)
        {
            _match = match;
        }

        public Task Task => _tcs.Task;

        public bool Matches((string Method, object?[] Args) broadcast) => _match(broadcast);

        public void Complete() => _tcs.TrySetResult();
    }

    private sealed class FakeHubClients : IHubClients
    {
        public FakeHubClients(Action<(string Method, object?[] Args)> onBroadcast)
        {
            AllProxy = new CapturingClientProxy(onBroadcast);
        }

        public CapturingClientProxy AllProxy { get; }

        public IClientProxy All => AllProxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllProxy;

        public IClientProxy Client(string connectionId) => AllProxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => AllProxy;

        public IClientProxy Group(string groupName) => AllProxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => AllProxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => AllProxy;

        public IClientProxy User(string userId) => AllProxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => AllProxy;
    }

    private sealed class CapturingClientProxy : IClientProxy
    {
        private readonly Action<(string Method, object?[] Args)> _onBroadcast;
        private readonly ConcurrentQueue<(string Method, object?[] Args)> _broadcasts = new();

        public CapturingClientProxy(Action<(string Method, object?[] Args)> onBroadcast)
        {
            _onBroadcast = onBroadcast;
        }

        public IReadOnlyCollection<(string Method, object?[] Args)> Broadcasts => [.. _broadcasts];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            var broadcast = (method, args);
            _broadcasts.Enqueue(broadcast);
            _onBroadcast(broadcast);

            return Task.CompletedTask;
        }
    }

    private sealed class NotSupportedGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Groups are not used in v1 dashboard push.");

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Groups are not used in v1 dashboard push.");
    }
}

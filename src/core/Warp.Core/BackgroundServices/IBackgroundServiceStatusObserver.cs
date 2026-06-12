namespace Warp.Core.BackgroundServices;

/// <summary>
/// Notification hook called by <c>BackgroundServiceSupervisor</c> after each
/// <see cref="BackgroundServiceStatus"/> transition is persisted. The default
/// production registration is a no-op singleton. Tests inject a custom
/// implementation to make state transitions deterministically observable.
/// </summary>
public interface IBackgroundServiceStatusObserver
{
    void OnStatusChanged(string serviceName, BackgroundServiceStatus status);
}

/// <summary>
/// Production default implementation of <see cref="IBackgroundServiceStatusObserver"/>. No-op
/// singleton registered by <c>AddWarpServer</c>. Tests replace it with a custom observer via DI.
/// External consumers that want to explicitly pass a no-op instance can reference this type
/// directly without relying on Warp internals.
/// </summary>
public sealed class NullBackgroundServiceStatusObserver : IBackgroundServiceStatusObserver
{
    public void OnStatusChanged(string serviceName, BackgroundServiceStatus status)
    {
        // No-op: production default. Tests replace via DI.
    }
}

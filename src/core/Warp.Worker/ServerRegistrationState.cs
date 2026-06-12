namespace Warp.Worker;

/// <summary>
/// In-process singleton that carries the DB-generated WorkerGroup IDs and assigned worker IDs
/// from <see cref="WarpServerRegistration{TContext}"/> to the worker host services. All three
/// are <c>IHostedService</c> implementations running in the same process; startup order is
/// guaranteed by DI registration order in <c>AddWarpServer</c>.
/// <para>
/// <see cref="Set"/> is expected to be called exactly once per process. A second call is a
/// bug — most likely a partial host recycle where the state singleton survived but the
/// workers spawned from the first state are still holding references to old IDs. Guarded
/// explicitly to surface the mistake loudly rather than silently diverge.
/// </para>
/// </summary>
public sealed class ServerRegistrationState
{
    private volatile IReadOnlyList<GroupRegistration> _groups = [];

    public IReadOnlyList<GroupRegistration> Groups => _groups;

    public void Set(IReadOnlyList<GroupRegistration> groups)
    {
        if (_groups.Count > 0)
        {
            throw new InvalidOperationException(
                "ServerRegistrationState.Set was called more than once. This singleton is populated once by WarpServerRegistration at host startup and read by the worker host services afterwards — a second Set indicates a partial host recycle with live workers holding stale IDs.");
        }

        _groups = groups;
    }

    public sealed record GroupRegistration(
        WorkerGroupConfiguration Config,
        Guid GroupEntityId,
        IReadOnlyList<Guid> WorkerIds);
}

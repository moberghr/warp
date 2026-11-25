using System.Collections.Concurrent;
using Warp.Worker;

namespace Warp.Tests.Helpers;

/// <summary>
/// A <see cref="PauseStateHolder"/> that knows what each worker loop last answered, so a test can wait until
/// a pause has actually reached every loop.
/// <para>
/// Pause is not a barrier (rule 6.8): a loop that read "not paused" just before the holder flipped is still in
/// its claim, and that claim can return a row the test publishes or requeues after the flip. A fixed wait for
/// such claims lost the race on CI more than once (SQL Server 2025: a claim stamped 600 ms before the job
/// existed came back with it). Once every loop of the group has answered "paused" since, none can claim.
/// </para>
/// <para>
/// Tells the loops apart without touching the worker: each loop is one async flow, and an AsyncLocal set inside
/// a synchronous call (<see cref="IsPaused"/>) stays set in the caller's flow, so a loop's first check gives it
/// an identity its later checks see again. <c>WarpTestServer</c> registers one for every server.
/// </para>
/// </summary>
internal sealed class WorkerLoopPauseTracker : PauseStateHolder, IDisposable
{
    private static readonly AsyncLocal<object?> Loop = new();
    private readonly ConcurrentDictionary<object, (Guid? Group, bool Paused)> _lastAnswer = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _checked = new(0);

    public override bool IsPaused(Guid? workerGroupId)
    {
        var paused = base.IsPaused(workerGroupId);
        _lastAnswer[Loop.Value ??= new object()] = (workerGroupId, paused);
        _checked.Release();

        return paused;
    }

    /// <summary>
    /// Returns once every loop that has checked at all (of <paramref name="workerGroupId"/> only, when given) has
    /// since answered "paused".
    /// </summary>
    public async Task WaitUntilEveryLoopSawPauseAsync(CancellationToken ct, Guid? workerGroupId = null)
    {
        while (_lastAnswer.Values.Any(x => !x.Paused && (workerGroupId is null || x.Group == workerGroupId)))
        {
            await _checked.WaitAsync(ct);
        }
    }

    public void Dispose() => _checked.Dispose();
}

using System.Collections.Concurrent;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

public class FailFirstAttemptRequest : IJob;

/// <summary>
/// Throws on a job's first execution and succeeds on every later one — the "retry recovered it" shape,
/// which no existing test handler produced (they either always throw or always succeed).
/// </summary>
/// <remarks>
/// The attempt bookkeeping is a static set keyed by job id rather than an injected service: the handler is
/// resolved from the per-job handler scope, so any per-attempt state has to outlive that scope, and a
/// registered singleton would then have to be added to every host that can resolve this handler
/// (<c>WarpTestServer</c> included) for an unrelated test to keep booting. Keying on the job id keeps
/// concurrent tests from interfering — ids are unique per test.
/// </remarks>
public class FailFirstAttemptCommand : IJobHandler<FailFirstAttemptRequest>
{
    private static readonly ConcurrentDictionary<Guid, byte> Attempted = new();

    private readonly IJobContext _jobContext;

    public FailFirstAttemptCommand(IJobContext jobContext) => _jobContext = jobContext;

    public Task HandleAsync(FailFirstAttemptRequest message, CancellationToken cancellationToken)
    {
        if (Attempted.TryAdd(_jobContext.JobId, 0))
        {
            throw new InvalidOperationException("Transient failure on the first attempt");
        }

        return Task.CompletedTask;
    }
}

using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

public class CancellableRequest : IJob;

public class CancellableCommand : IJobHandler<CancellableRequest>
{
    public async Task HandleAsync(CancellableRequest message, CancellationToken cancellationToken)
    {
        // Simulate long-running work that respects cancellation
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
}

// Same shape, but the handler carries a policy so the pipeline STAMPS metadata during the attempt —
// the cancellation path must persist that stamp exactly like the success and failure paths do.
public class CancellableStampedRequest : IJob;

[Retry(2)]
public class CancellableStampedCommand : IJobHandler<CancellableStampedRequest>
{
    public async Task HandleAsync(CancellableStampedRequest message, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
    }
}

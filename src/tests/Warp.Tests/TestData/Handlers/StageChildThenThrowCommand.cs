using Warp.Core;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Stages one <see cref="UnitRequest"/> and then throws: the staged job is rightly discarded with the
/// failed attempt.
/// </summary>
public class StageChildThenThrowRequest : IJob;

public class StageChildThenThrowHandler : IJobHandler<StageChildThenThrowRequest>
{
    private readonly IPublisher _publisher;

    public StageChildThenThrowHandler(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task HandleAsync(StageChildThenThrowRequest message, CancellationToken cancellationToken)
    {
        await _publisher.Enqueue(new UnitRequest());

        throw new InvalidOperationException("failed after staging a job");
    }
}

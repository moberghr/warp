using Warp.Core;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Stages one <see cref="UnitRequest"/> and does not save. The worker does not save a handler's context,
/// so the staged job is discarded with the handler's scope.
/// </summary>
public class StageChildWithoutSavingRequest : IJob;

public class StageChildWithoutSavingHandler : IJobHandler<StageChildWithoutSavingRequest>
{
    private readonly IPublisher _publisher;

    public StageChildWithoutSavingHandler(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task HandleAsync(StageChildWithoutSavingRequest message, CancellationToken cancellationToken)
    {
        await _publisher.Enqueue(new UnitRequest());
    }
}

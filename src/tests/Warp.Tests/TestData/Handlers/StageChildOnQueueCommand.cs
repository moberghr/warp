using Warp.Core;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Stages one <see cref="UnitRequest"/> on <see cref="Queue"/> and does NOT save: the worker's
/// handler-scope save is what commits it. Used to check that exactly one wake-up goes out for it.
/// </summary>
public class StageChildOnQueueRequest : IJob
{
    public string Queue { get; set; } = "default";
}

public class StageChildOnQueueHandler : IJobHandler<StageChildOnQueueRequest>
{
    private readonly IPublisher _publisher;

    public StageChildOnQueueHandler(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task HandleAsync(StageChildOnQueueRequest message, CancellationToken cancellationToken)
    {
        await _publisher.Enqueue(new UnitRequest(), message.Queue);
    }
}

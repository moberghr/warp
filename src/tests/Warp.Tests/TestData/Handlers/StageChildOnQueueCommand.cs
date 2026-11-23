using Warp.Core;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Stages one <see cref="UnitRequest"/> on <see cref="Queue"/> and saves it, as a handler must: the
/// worker does not save a handler's context. Used to check that exactly one wake-up goes out for it.
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
        await _publisher.SaveChangesAsync(cancellationToken);
    }
}

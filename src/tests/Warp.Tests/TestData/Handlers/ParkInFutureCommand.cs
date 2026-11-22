using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

public class ParkInFutureRequest : IJob;

/// <summary>
/// Reschedules its own job a day ahead and asks for the handler type to be cleared. The row then rests in
/// <c>Scheduled</c>, so a test can read what the finalizing write did with an outcome's
/// <c>ScheduleTime</c> and <c>ClearHandlerType</c> without racing a second execution.
/// </summary>
public class ParkInFutureCommand : IJobHandler<ParkInFutureRequest>
{
    private readonly IJobContext _jobContext;

    public ParkInFutureCommand(IJobContext jobContext) => _jobContext = jobContext;

    public Task HandleAsync(ParkInFutureRequest message, CancellationToken cancellationToken)
    {
        _jobContext.Outcome = new JobOutcome
        {
            State = State.Scheduled,
            ScheduleTime = DateTime.UtcNow.AddDays(1),
            ClearHandlerType = true,
            LogMessage = "Parked for a day",
        };

        return Task.CompletedTask;
    }
}

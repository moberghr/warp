using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Worker.Services;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class ServerTaskStartupDelayTests
{
    [TimedTheory]
    [InlineData(60, 10, 0.5, 5)]
    [InlineData(4, 10, 0.5, 2)]
    [InlineData(60, 10, 1.0, 10)]
    [InlineData(60, 10, 0.0, 0)]
    [InlineData(60, 0, 0.5, 0)]
    [InlineData(60, 10, 7.0, 10)]
    public void FirstRunDelay_SampleOfTheSmallerOfIntervalAndStartupDelay(int intervalSeconds, int delaySeconds, double sample, double expectedSeconds)
    {
        // A task's first run waits a random part of the smaller of its own interval and the configured
        // startup delay; zero disables it, and a sample outside 0-1 is clamped.
        var delay = ClusterTaskTimings.FirstRunDelay(
            TimeSpan.FromSeconds(intervalSeconds),
            TimeSpan.FromSeconds(delaySeconds),
            sample);

        delay.ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }
}

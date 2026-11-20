using Microsoft.Extensions.Options;
using Warp.Core.RateLimit;

namespace Warp.Worker;

// Hands this server's ScheduledJobActivation cadence to the rate-limit gate, so throttled Wait-mode
// jobs are not stepped closer together than one activation tick (see IScheduledActivationCadence).
internal sealed class ScheduledActivationCadence : IScheduledActivationCadence
{
    private readonly IOptions<WarpServerConfiguration> _configuration;

    public ScheduledActivationCadence(IOptions<WarpServerConfiguration> configuration)
    {
        _configuration = configuration;
    }

    public TimeSpan Interval => _configuration.Value.ScheduledActivationInterval;
}

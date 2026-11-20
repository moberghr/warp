namespace Warp.Core.RateLimit;

// How often this process's ScheduledJobActivation flips due Scheduled rows back to Enqueued. A throttled
// Wait-mode job comes back through it, so slots closer together than one activation tick are released
// together anyway: RateLimitGate steps a key's slots no closer than this. Registered by the worker
// (Warp.Worker owns the setting); injected as IEnumerable, so a process without a worker — which runs
// no job pipeline — sees none and keeps plain window spacing (the optional-marker shape, §2.9).
internal interface IScheduledActivationCadence
{
    TimeSpan Interval { get; }
}

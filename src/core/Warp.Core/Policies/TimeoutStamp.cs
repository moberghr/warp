namespace Warp.Core.Policies;

/// <summary>Outcome of resolving the timeout family for one execution (§8.8).</summary>
internal enum TimeoutStamp
{
    AlreadyResolved = 1,

    NothingDeclared = 2,

    Stamped = 3,

    /// <summary>A contract-declared Total timeout reached execution with no publish-stamped deadline.</summary>
    TotalWithoutDeadline = 4,

    /// <summary>
    /// A handler-declared Total timeout: a wall-clock budget measured from enqueue cannot be resolved once
    /// the job is running. Inert, never a throw — the resolver runs inside the pipeline, where an outer
    /// Retry would treat the exception as a handler failure and burn the whole budget on a static
    /// misconfiguration. WARP002 is the build-time gate; this is the backstop for handlers it cannot see.
    /// </summary>
    TotalOnHandler = 5,
}

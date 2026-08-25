namespace Warp.Core.Policies;

/// <summary>Outcome of resolving the timeout family for one execution (§8.8).</summary>
internal enum TimeoutStamp
{
    AlreadyResolved = 1,

    NothingDeclared = 2,

    Stamped = 3,

    /// <summary>A contract-declared Total timeout reached execution with no publish-stamped deadline.</summary>
    TotalWithoutDeadline = 4,
}

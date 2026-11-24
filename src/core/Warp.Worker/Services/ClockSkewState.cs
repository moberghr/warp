namespace Warp.Worker.Services;

/// <summary>
/// Whether this server's clock was last seen past <c>ClockSkewWarningThreshold</c> (#350). A singleton: the
/// heartbeat is scoped, and a skew is reported once per excursion, not on every beat.
/// </summary>
public sealed class ClockSkewState
{
    private int _skewed;

    /// <summary>Records the latest reading; true only when it starts an excursion past the threshold.</summary>
    public bool Enter(bool skewed)
    {
        var previous = Interlocked.Exchange(ref _skewed, skewed ? 1 : 0);

        return skewed && previous == 0;
    }
}

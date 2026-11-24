namespace Warp.Core;

/// <summary>
/// The database's clock, as seen from this process: the local clock plus the offset the server's heartbeat
/// last measured against the database (#350). Server components use it for every time another server reads
/// or compares — keep-alives, heartbeats, claim stamps, schedule activation, recurring firings, lease expiry,
/// stale and expiry cutoffs — so a server whose own clock is wrong cannot requeue another server's running
/// job, remove a live server, or fire a schedule early. Only <see cref="GetUtcNow"/> is corrected: timers and
/// timestamps (durations) stay on the local clock.
/// <para>
/// Server components receive it as a keyed <see cref="TimeProvider"/> (<see cref="Key"/>), so their
/// constructors still take a plain <see cref="TimeProvider"/> and a test can pass any clock. The offset is
/// zero until the first heartbeat measures it, and stays zero when the heartbeat is disabled
/// (<c>HealthCheckInterval = null</c>).
/// </para>
/// <para>
/// While a server from before 8.0 is alive (<see cref="LegacyServerPresence"/>) the offset is still measured
/// but not applied: that server compares its own clock, so this one does too, as 7.7 did (rule 2.20).
/// </para>
/// </summary>
public sealed class DatabaseClock : TimeProvider
{
    /// <summary>The service key server components resolve their <see cref="TimeProvider"/> under.</summary>
    public const string Key = "warp:database-clock";

    /// <summary>
    /// A reading of the database clock is worth recording only when its round trip was at most this long:
    /// the database read its clock somewhere inside it, so the error is up to half of it.
    /// </summary>
    public static readonly TimeSpan MaxSampleRoundTrip = TimeSpan.FromSeconds(2);

    private long _offsetTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseClock"/> class that always applies the measured
    /// offset: it knows of no server from before 8.0.
    /// </summary>
    public DatabaseClock(TimeProvider local)
        : this(local, new LegacyServerPresence(alive: false))
    {
    }

    internal DatabaseClock(TimeProvider local, LegacyServerPresence legacyServers)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(legacyServers);
        Local = local;
        LegacyServers = legacyServers;
    }

    /// <summary>The process's own clock, which the offset corrects.</summary>
    public TimeProvider Local { get; }

    /// <summary>The database clock minus this process's clock, as last measured (applied or not).</summary>
    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>Whether a server from before 8.0 is alive, which suspends the correction. Removable with it.</summary>
    internal LegacyServerPresence LegacyServers { get; }

    public override TimeZoneInfo LocalTimeZone => Local.LocalTimeZone;

    public override long TimestampFrequency => Local.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => LegacyServers.Alive ? Local.GetUtcNow() : Local.GetUtcNow() + Offset;

    public override long GetTimestamp() => Local.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        Local.CreateTimer(callback, state, dueTime, period);

    /// <summary>
    /// Records a reading of the database clock taken by a statement sent at <paramref name="sent"/> and
    /// answered at <paramref name="received"/>, both on <see cref="Local"/>. The database read its clock
    /// somewhere in between, so it is compared with the midpoint: the error is at most half the round trip.
    /// </summary>
    public TimeSpan Record(DateTimeOffset sent, DateTimeOffset received, DateTime databaseUtcNow)
    {
        var midpoint = sent + ((received - sent) / 2);
        var offset = new DateTimeOffset(DateTime.SpecifyKind(databaseUtcNow, DateTimeKind.Utc)) - midpoint;
        Interlocked.Exchange(ref _offsetTicks, offset.Ticks);

        return offset;
    }
}

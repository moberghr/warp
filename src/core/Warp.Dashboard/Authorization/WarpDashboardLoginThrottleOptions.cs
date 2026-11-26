namespace Warp.Dashboard;

/// <summary>
/// Throttling of the built-in login endpoint, configured through
/// <c>AddBuiltInLogin&lt;TValidator&gt;(o =&gt; o.Throttling...)</c>. Every attempt counts against both the
/// caller's IP address and the submitted username; once either has used up <see cref="FreeAttempts"/>, it is
/// locked out for <see cref="LockoutDuration"/>, doubling on each further attempt up to
/// <see cref="MaxLockoutDuration"/>. A successful sign-in clears both. A locked-out attempt is answered with
/// 429 and a <c>Retry-After</c> header, without calling the credential validator.
/// </summary>
/// <remarks>
/// The state is in memory and per process: with several instances behind a load balancer each keeps its own
/// count. Behind a reverse proxy that does not forward the client address (no <c>UseForwardedHeaders</c>),
/// every caller shares the proxy's IP and therefore one IP budget. For a cluster-wide limit, attach your own
/// ASP.NET Core rate-limiting policy to the dashboard: <c>app.MapWarpDashboard("/warp").RequireRateLimiting("policy")</c>.
/// </remarks>
public class WarpDashboardLoginThrottleOptions
{
    /// <summary>Whether login attempts are throttled. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Attempts an IP address or a username gets before the first lockout. Default 5.</summary>
    public int FreeAttempts { get; set; } = 5;

    /// <summary>First lockout once <see cref="FreeAttempts"/> are used up. Doubles with each further attempt. Default 30 seconds.</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound of the escalating lockout. Default 15 minutes.</summary>
    public TimeSpan MaxLockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an IP address or username is remembered after its last attempt, once no lockout is in force.
    /// After this the count starts over. Default 15 minutes.
    /// </summary>
    public TimeSpan ForgetAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Most IP addresses and, separately, usernames tracked at once. Both are caller-controlled, so the table is
    /// bounded: when it is full, entries that are no longer locked out and past <see cref="ForgetAfter"/> are
    /// dropped first, then the least recently used. Default 10,000.
    /// </summary>
    public int MaxTrackedKeys { get; set; } = 10_000;
}

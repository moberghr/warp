namespace Warp.Core.Adapters;

/// <summary>
/// Chain-walking helpers for recognising a shared-rate-limit refusal that reached the caller wrapped.
/// <para>
/// A refusal arrives as <see cref="AdapterRateLimitedException"/> on the plain <c>HttpClient</c> path, but
/// clients that wrap pipeline exceptions hand the caller something else — Refit wraps every exception
/// escaping the pipeline in <c>ApiRequestException</c>, so matching on the type alone silently never fires.
/// These helpers match on the chain instead, which stays correct either way.
/// </para>
/// <code>
/// catch (Exception ex) when (ex.IsAdapterRateLimited())
/// {
///     var wait = ex.GetAdapterRetryAfter() ?? TimeSpan.FromSeconds(1);
/// }
/// </code>
/// <para>
/// An adapter configured with <c>AdapterRateLimitOverflow.Respond429</c> needs none of this — the refusal
/// arrives as an ordinary <c>429</c> response that existing status-based classification already handles.
/// </para>
/// </summary>
public static class AdapterRateLimitExtensions
{
    /// <summary>
    /// True when <paramref name="exception"/> is, or wraps at any depth, an
    /// <see cref="AdapterRateLimitedException"/> — walking <see cref="Exception.InnerException"/> and every
    /// branch of an <see cref="AggregateException"/>. Null reads as false.
    /// </summary>
    public static bool IsAdapterRateLimited(this Exception? exception) => Find(exception) is not null;

    /// <summary>
    /// The <see cref="AdapterRateLimitedException.RetryAfter"/> of the rate-limit refusal in
    /// <paramref name="exception"/>'s chain, or null when there is none (or the refusal carried no timing).
    /// </summary>
    public static TimeSpan? GetAdapterRetryAfter(this Exception? exception) => Find(exception)?.RetryAfter;

    /// <summary>
    /// The <see cref="AdapterRateLimitedException"/> in <paramref name="exception"/>'s chain, or null when
    /// the chain holds none.
    /// </summary>
    public static AdapterRateLimitedException? FindAdapterRateLimited(this Exception? exception) => Find(exception);

    private static AdapterRateLimitedException? Find(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AdapterRateLimitedException rateLimited)
            {
                return rateLimited;
            }

            // An AggregateException's InnerException is only its first branch, so walk them all — a refusal
            // raised from one of several concurrent sends would otherwise be missed.
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find(inner) is { } found)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }
}

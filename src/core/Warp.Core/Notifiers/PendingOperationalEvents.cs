namespace Warp.Core.Notifiers;

/// <summary>
/// Scoped buffer for operational events raised by a server task whose <c>ExecuteAsync</c> runs INSIDE the
/// server-task host's lock transaction. Those tasks must not dispatch notifiers directly — their
/// <c>SaveChangesAsync</c> only flushes into the outer transaction, which the host commits AFTER
/// <c>ExecuteAsync</c> returns, so a direct dispatch would fire pre-commit and could alert on a change a
/// rollback then undoes (violating the §8.20 post-commit contract). Instead the task <see cref="Add"/>s its
/// events here and the host <see cref="Drain"/>s + dispatches them once the transaction has committed.
/// <para>
/// Registered <b>scoped</b> by <c>AddWarp</c> so each server-task iteration gets its own buffer, discarded
/// with the scope if the transaction rolls back (the drain only runs on the committed path).
/// </para>
/// </summary>
public sealed class PendingOperationalEvents
{
    private readonly List<WarpOperationalEvent> _events = [];

    /// <summary>Buffer an event to be dispatched by the host after the current transaction commits.</summary>
    public void Add(WarpOperationalEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        _events.Add(evt);
    }

    /// <summary>Returns the buffered events and clears the buffer.</summary>
    public IReadOnlyList<WarpOperationalEvent> Drain()
    {
        if (_events.Count == 0)
        {
            return [];
        }

        var drained = _events.ToArray();
        _events.Clear();

        return drained;
    }
}

using Microsoft.Extensions.Logging;

namespace Warp.Core;

// Starts an IHostedService's work (a worker host's loops, a seeder's pass) now when the server is registered (always
// with RequireDatabaseAtStartup on, and always in a process without a server), or once the startup gate opens when
// registration is still retrying in the background. Nothing here runs per job: the wait happens once, before any
// loop exists (§0.2). BackgroundService loops wait with WarpStartupGate.WaitForServerAsync instead.
internal sealed class GatedStart
{
    private readonly WarpStartupGate? _gate;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pending;

    public GatedStart(WarpStartupGate? gate, ILogger logger)
    {
        _gate = gate;
        _logger = logger;
    }

    // Test seam: the deferred start, null when the loops started inside StartAsync.
    internal Task? Pending => _pending;

    public Task StartAsync(Func<CancellationToken, Task> start, CancellationToken cancellationToken)
    {
        if (_gate is null || _gate.IsOpen)
        {
            return start(cancellationToken);
        }

        _pending = StartWhenOpenAsync(_gate, start);

        return Task.CompletedTask;
    }

    // Before the host stops what it started: a start still waiting is abandoned, one in progress finishes, so the
    // host then stops every loop that exists.
    public async Task StopAsync()
    {
        _stopRequested.TrySetResult();
        if (_pending is not null)
        {
            await _pending;
        }
    }

    private async Task StartWhenOpenAsync(WarpStartupGate gate, Func<CancellationToken, Task> start)
    {
        try
        {
            var opened = gate.WaitUntilOpenAsync(CancellationToken.None);
            if (await Task.WhenAny(opened, _stopRequested.Task) != opened)
            {
                return;
            }

            await start(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Warp could not start this service after the server registered.");
        }
    }
}

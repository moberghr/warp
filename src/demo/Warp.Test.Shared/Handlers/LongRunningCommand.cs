using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Warp.Core.Handlers;

/// <summary>
/// A job that runs for <see cref="DurationMs"/> and writes a log line every <see cref="LogIntervalMs"/>, so a
/// benchmark can hold many jobs running at once past the server's keep-alive interval. What it measures is
/// the worker's per-group monitor (rule 6.14): the keep-alive renewal and the handler-log flush.
/// </summary>
public class LongRunningRequest : IJob
{
    public int DurationMs { get; set; }

    public int LogIntervalMs { get; set; }
}

public class LongRunningCommand : IJobHandler<LongRunningRequest>
{
    private readonly ILogger<LongRunningCommand> _logger;

    public LongRunningCommand(ILogger<LongRunningCommand> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(LongRunningRequest message, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var step = 0;

        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < message.DurationMs)
        {
            if (message.LogIntervalMs > 0)
            {
                _logger.LogInformation("Long-running step {Step}", step++);
            }

            var remaining = message.DurationMs - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var wait = message.LogIntervalMs > 0 ? Math.Min(message.LogIntervalMs, remaining) : remaining;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(wait), cancellationToken);
            }
        }
    }
}

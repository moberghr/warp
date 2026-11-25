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
        if (message.LogIntervalMs <= 0)
        {
            await Task.Delay(message.DurationMs, cancellationToken);

            return;
        }

        // A FIXED number of lines, each step sleeping until its own deadline. The loop used to run until the
        // duration had elapsed, sleeping min(interval, remaining): when less than a millisecond remained,
        // Task.Delay truncated it to zero and completed at once, so the job spun out up to a few hundred extra
        // log lines in its last millisecond. Each is a job_log row (an INSERT in single-worker mode, WAL in
        // both), so the scenario's statements and WAL per job swung by 2x between runs of the same code.
        var started = Stopwatch.GetTimestamp();
        var steps = Math.Max(1, message.DurationMs / message.LogIntervalMs);

        for (var step = 0; step < steps; step++)
        {
            _logger.LogInformation("Long-running step {Step}", step);

            var wait = ((step + 1) * message.LogIntervalMs) - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (wait > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Ceiling(wait)), cancellationToken);
            }
        }
    }
}

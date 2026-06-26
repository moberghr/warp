using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;
using Warp.Core.Enums;

namespace Warp.Worker;

/// <summary>
/// Batch-fetches jobs from the database and distributes them to worker slots via a channel.
/// One dispatcher per worker group. Workers execute handlers and complete jobs individually.
/// </summary>
public class WarpDispatcher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WarpDispatcher<TContext>> _logger;
    private readonly WorkerGroupConfiguration _groupConfiguration;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<Job> _jobChannel;
    private readonly int _workerCount;
    private readonly PauseStateHolder _pauseStateHolder;
    private readonly Guid _workerGroupId;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly IDisposable _registration;

    public WarpDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<WarpDispatcher<TContext>> logger,
        IOptions<WarpServerConfiguration> configuration,
        WorkerGroupConfiguration groupConfiguration,
        TimeProvider timeProvider,
        PauseStateHolder pauseStateHolder,
        Guid workerGroupId,
        DispatcherRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _groupConfiguration = groupConfiguration;
        _timeProvider = timeProvider;
        _workerCount = groupConfiguration.WorkerCount;
        _pauseStateHolder = pauseStateHolder;
        _workerGroupId = workerGroupId;

        _jobChannel = Channel.CreateBounded<Job>(new BoundedChannelOptions(_workerCount)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
        });

        _registration = registry.Register(_signal);
    }

    public ChannelReader<Job> JobReader => _jobChannel.Reader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var floor = _groupConfiguration.PollingInterval;
        var max = _groupConfiguration.MaxPollingInterval;
        var factor = _groupConfiguration.PollingIntervalFactor;
        var currentDelay = floor;

        // Cleanup MUST run even if an unexpected exception escapes the loop body — the channel
        // writer must be completed so DispatcherWorkers waiting on WaitToReadAsync can exit.
        // A leaked exception that skipped this would block IHost.StopAsync until ShutdownTimeout
        // (default 30s) fires, masking the real failure.
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_pauseStateHolder.IsPaused(_workerGroupId))
                    {
                        currentDelay = floor;
                        await _signal.WaitAsync(floor, stoppingToken);
                        continue;
                    }

                    var result = await FetchAndDistribute(stoppingToken);
                    if (result == FetchResult.Empty)
                    {
                        currentDelay = PollingBackoff.Next(currentDelay, floor, max, factor);
                        await _signal.WaitAsync(currentDelay, stoppingToken);
                        continue;
                    }

                    if (result == FetchResult.Fetched)
                    {
                        // Real work happened — reset the idle backoff. ChannelFull is not evidence
                        // of work done (workers are saturated), so leave currentDelay alone; the
                        // 10ms wait inside FetchAndDistribute is the channel-full throttle.
                        currentDelay = floor;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Exception is a transient signal, not an idle-queue signal — do not compound
                    // the polling backoff. A single floor delay keeps the dispatcher responsive
                    // after recovery instead of sitting at MaxPollingInterval for 30s. The wait is
                    // wrapped in its own try/catch so an OCE during shutdown doesn't escape and
                    // skip the writer cleanup below.
                    _logger.LogError(ex, "Dispatcher fetch failed");
                    try
                    {
                        await Task.Delay(floor, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            _jobChannel.Writer.Complete();
            _registration.Dispose();
        }
    }

    private async Task<FetchResult> FetchAndDistribute(CancellationToken ct)
    {
        var available = _workerCount - _jobChannel.Reader.Count;
        if (available <= 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
            return FetchResult.ChannelFull;
        }

        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var sqlQueries = scope.ServiceProvider.GetRequiredService<IWarpSqlQueries<TContext>>();

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        // Atomic batch claim. One round-trip, no SELECT→UPDATE window — returns up to
        // `available` rows already transitioned to Processing. Workers across servers get
        // distinct rows via FOR UPDATE SKIP LOCKED (PG) / ROWLOCK+UPDLOCK+READPAST (MSSQL)
        // in the claim subquery.
        // Mutex enforcement runs inside the handler pipeline (MutexPipelineBehavior) — same
        // as before; it short-circuits to Deleted if the concurrency key is held.
        var jobs = await sqlQueries.ClaimEnqueuedJobsAsync(
            context,
            _groupConfiguration.Queues,
            _workerGroupId,
            now,
            available,
            ct);

        if (jobs.Count == 0)
        {
            return FetchResult.Empty;
        }

        // The atomic claim already committed State=Processing for every row in `jobs`. Delivering
        // them to the channel is a separate, interruptible phase: each WriteAsync is an await
        // point where a shutdown-triggered cancellation can fire. If we don't recover,
        // claimed-but-undelivered rows stay as Processing orphans until StaleJobRecovery finds
        // them. The try/catch here restores undelivered rows back to Enqueued so shutdown leaves
        // the DB in a clean state.
        // The "Processing" JobLog is intentionally NOT written here — it's written by the
        // receiving worker in MarkWorkerOwnership after channel delivery succeeds. Writing it
        // here would leave orphan log rows for jobs whose channel-write got cancelled
        // (the row reverts to Enqueued via UnclaimUndelivered, but a "Processing" log would
        // remain). Writing it on the worker side keeps the JobLog accurate and lets us tag it
        // with the specific WorkerId, matching single-worker-mode semantics.
        var delivered = 0;
        try
        {
            foreach (var job in jobs)
            {
                await _jobChannel.Writer.WriteAsync(job, ct);
                delivered++;
            }
        }
        catch
        {
            if (delivered < jobs.Count)
            {
                await UnclaimUndelivered(jobs, delivered);
            }

            throw;
        }

        return FetchResult.Fetched;
    }

    /// <summary>
    /// Flips the tail of <paramref name="jobs"/> (rows starting at index <paramref name="delivered"/>)
    /// back to <see cref="State.Enqueued"/> in the DB, clearing the claim stamp. Used when delivery
    /// to the channel is interrupted — most commonly by shutdown cancellation — so the rows don't
    /// linger as Processing orphans. Uses a fresh scope + <see cref="CancellationToken.None"/> so
    /// the cleanup is never itself cancelled.
    /// </summary>
    private async Task UnclaimUndelivered(List<Job> jobs, int delivered)
    {
        try
        {
            var undeliveredIds = jobs.Skip(delivered).Select(j => j.Id).ToArray();
            using var cleanupScope = _scopeFactory.CreateScope();
            var cleanupContext = cleanupScope.ServiceProvider.GetRequiredService<TContext>();

            await cleanupContext.Set<Job>()
                .Where(x => undeliveredIds.Contains(x.Id))
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(p => p.CurrentState, State.Enqueued)
                        .SetProperty(p => p.CurrentWorkerId, (Guid?)null)
                        .SetProperty(p => p.LastKeepAlive, (DateTime?)null),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Cleanup failure is not fatal — StaleJobRecovery will still recover the orphaned
            // Processing rows on its normal cadence. Log loudly because it means the DB was in
            // a bad state (connection lost, timeout) exactly when we needed it.
            _logger.LogError(ex, "Failed to un-claim {Count} undelivered jobs; StaleJobRecovery will recover", jobs.Count - delivered);
        }
    }

    private enum FetchResult
    {
        Empty,
        Fetched,
        ChannelFull,
    }
}

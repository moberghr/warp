using Warp.Core;

namespace Warp.Worker;

public class WorkerGroupConfiguration
{
    public int WorkerCount { get; set; } = Math.Min(Environment.ProcessorCount * 5, 20);

    public string[] Queues { get; set; } = ["default"];

    /// <summary>
    /// Each time the worker polls for a job, it will wait for this interval before polling again.
    /// Also serves as the floor for exponential backoff when consecutive polls return no work.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the polling delay when consecutive polls return no work.
    /// The delay grows from <see cref="PollingInterval"/> by <see cref="PollingIntervalFactor"/>
    /// on each empty poll, clamped to this value. Resets to <see cref="PollingInterval"/>
    /// instantly when a job is processed.
    /// </summary>
    public TimeSpan MaxPollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Multiplier applied to the current polling delay on each consecutive empty poll.
    /// Set to 1.0 (or lower) to disable exponential backoff — the delay stays at
    /// <see cref="PollingInterval"/>.
    /// </summary>
    public double PollingIntervalFactor { get; set; } = 2.0;
}

public class WarpWorkerConfiguration : WarpConfiguration
{
    private static readonly int DefaultWorkerCount = Math.Min(Environment.ProcessorCount * 5, 20);

    /// <summary>
    /// How many worker instances should be created. Applies to the implicit default worker group.
    /// </summary>
    public int WorkerCount { get; set; } = DefaultWorkerCount;

    /// <summary>
    /// Each time the worker polls for a job, it will wait for this interval before polling again.
    /// Applies to the implicit default worker group. Also serves as the floor for exponential
    /// backoff when consecutive polls return no work.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Upper bound on the polling delay when consecutive polls return no work.
    /// Applies to the implicit default worker group.
    /// </summary>
    public TimeSpan MaxPollingInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Multiplier applied to the current polling delay on each consecutive empty poll.
    /// Set to 1.0 (or lower) to disable exponential backoff. Applies to the implicit default worker group.
    /// </summary>
    public double PollingIntervalFactor { get; set; } = 2.0;

    /// <summary>
    /// Queues this worker subscribes to. Applies to the implicit default worker group.
    /// </summary>
    public string[] Queues { get; set; } = ["default"];

    /// <summary>
    /// Upper bound on how many rows <see cref="Services.MessageRouter{TContext}"/> and
    /// <see cref="Services.Orchestrator{TContext}"/> process in a single ExecuteAsync call.
    /// When the limit is hit the task returns and the host re-ticks immediately (RerunImmediately
    /// = true) — bounded latency keeps cancellation responsive and prevents one server from
    /// hogging the lock through a huge backlog. The trade-off is multi-server fairness: a larger
    /// value drains backlogs faster on the routing server but keeps the routing advisory lock
    /// held longer, so peer servers wait longer to take a turn. Tune down if you run many
    /// routing servers against the same DB and notice one server monopolising work.
    /// </summary>
    public int ServerTaskBatchSize { get; set; } = 1000;

    /// <summary>
    /// Cadence at which <see cref="Services.Heartbeat{TContext}"/> refreshes
    /// <c>LastHeartbeatTime</c> and re-reads <c>PausedAt</c> into the in-memory
    /// <see cref="PauseStateHolder"/>. Set to <c>null</c> to disable the auto-run loop —
    /// useful for tests that drive the heartbeat tick manually via
    /// <c>ServerTaskHost.RunOnceAsync&lt;Heartbeat&gt;</c>.
    /// </summary>
    public TimeSpan? HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(3);

    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often <see cref="Services.CounterAggregator{TContext}"/> folds pending Counter rows
    /// into the Statistic table. Set to <c>null</c> to disable the auto-run loop — the task
    /// stays DI-resolvable but no server runs it on a schedule.
    /// <para>
    /// Dashboard counter graphs refresh at this cadence. The default is 1 minute because
    /// counter aggregation is not latency-critical; tighten it if you need fresher dashboard
    /// stats and don't mind the extra DB chatter (one SELECT every interval).
    /// </para>
    /// </summary>
    public TimeSpan? CounterAggregationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often <see cref="Services.ServerCleanup{TContext}"/> removes Server rows whose
    /// heartbeat is past <see cref="HealthCheckTimeout"/>. Set to <c>null</c> to disable.
    /// </summary>
    public TimeSpan? ServerCleanupInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often <see cref="Services.StaleJobRecovery{TContext}"/> requeues or fails jobs
    /// whose worker stopped refreshing keep-alive. Set to <c>null</c> to disable.
    /// </summary>
    public TimeSpan? StaleJobRecoveryInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often <see cref="Services.ExpirationCleanup{TContext}"/> deletes expired jobs and
    /// their log rows. Set to <c>null</c> to disable.
    /// </summary>
    public TimeSpan? ExpirationCleanupInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How often <see cref="Services.RecurringJobScheduler{TContext}"/> checks for recurring
    /// jobs whose NextExecution has elapsed and creates the next occurrence. Set to
    /// <c>null</c> to disable.
    /// </summary>
    public TimeSpan? RecurringJobSchedulerInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How often the <see cref="Services.Orchestrator{TContext}"/> task runs to finalize
    /// parents whose children all reached terminal state, activate continuations, and fail
    /// children of deleted parents. Set to <c>null</c> to disable the periodic auto-loop —
    /// orchestration is then driven entirely by <c>JobFinalized</c> push signals plus any
    /// explicit ticks (e.g. <c>WarpTestServer.RunOrchestratorOnceAsync</c> in tests).
    /// </summary>
    public TimeSpan? OrchestrationInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the <see cref="Services.MessageRouter{TContext}"/> task runs to discover
    /// handlers for newly-enqueued <c>Kind=Message</c> rows. Set to <c>null</c> to disable
    /// the periodic auto-loop — routing is then driven entirely by <c>MessageEnqueued</c>
    /// push signals plus any explicit ticks.
    /// </summary>
    public TimeSpan? MessageRoutingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often the scheduled-job activation task checks for rows in <see cref="Core.Enums.State.Scheduled"/>
    /// whose <c>ScheduleTime</c> has elapsed and flips them to <see cref="Core.Enums.State.Enqueued"/>.
    /// </summary>
    public TimeSpan ScheduledActivationInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the worker checks if a running job has been cancelled (deleted).
    /// Also refreshes the keep-alive timestamp on each check.
    /// </summary>
    public TimeSpan CancellationCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the job monitor drains buffered handler logs into the JobLog table.
    /// Lower values surface dashboard logs faster at the cost of more DB writes; tests
    /// may tune this down to avoid multi-second sleeps.
    /// </summary>
    public TimeSpan LogFlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a job can go without a keep-alive refresh before being considered stale and requeued.
    /// Workers refresh keep-alive every InvisibilityTimeout / 5 during execution.
    /// </summary>
    public TimeSpan InvisibilityTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// When a job's worker dies and the job is recovered, by default it is requeued (true).
    /// Set to false to fail stale jobs by default. Can be overridden per-job with
    /// [NoRestart]/[Restart] attributes or .WithRestart(bool).
    /// </summary>
    public bool RestartStaleJobsByDefault { get; set; } = true;

    /// <summary>
    /// Worker Id should be unique for each worker. If you need to control the worker id, you can set it here.
    /// </summary>
    public Guid ServerId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Display name for this server in the dashboard. Defaults to MachineName.
    /// </summary>
    public string? ServerName { get; set; }

    public int ExpirationBatchSize { get; set; } = 1000;

    /// <summary>
    /// Maximum number of jobs with a non-null ExpireAt to retain.
    /// When exceeded, the oldest by ExpireAt are deleted first until at threshold.
    /// Failed jobs are excluded (they have null ExpireAt). Null to disable (default).
    /// </summary>
    public int? MaxExpirableJobCount { get; set; }

    /// <summary>
    /// When true (default), handler ILogger output is captured and written to the JobLog table.
    /// When false, only system state-transition logs (Processing, Completed, Failed, etc.) are written.
    /// Disabling reduces database write overhead for high-throughput workloads.
    /// </summary>
    public bool EnableHandlerLogging { get; set; } = true;

    /// <summary>
    /// When true, uses a single dispatcher per worker group that batch-fetches jobs
    /// and distributes them to workers, reducing per-job DB overhead.
    /// When false (default), each worker independently fetches its own jobs.
    /// </summary>
    public bool UseDispatcher { get; set; }

    /// <summary>
    /// Dispatcher-mode only. Maximum number of job completions each worker buffers in memory
    /// before flushing them to the database in a single transaction. Defaults to 50.
    /// Set to 1 to disable batching (every completion commits in its own transaction).
    /// <para>
    /// Trade-off: batching widens the at-least-once duplicate-execution window. If a worker
    /// crashes with buffered completions that have not yet been flushed, those jobs stay in
    /// <c>Processing</c> and <c>StaleJobRecovery</c> will requeue them per the
    /// <c>[NoRestart]</c> setting. Handlers with side effects should be idempotent or marked
    /// <c>[NoRestart]</c>.
    /// </para>
    /// </summary>
    public int CompletionBatchSize { get; set; } = 50;

    /// <summary>
    /// Dispatcher-mode only. Maximum time a buffered completion may wait before being flushed.
    /// The timer starts when the first entry is added to an empty buffer. Defaults to 100ms.
    /// <para>
    /// A longer interval batches more completions (lower DB overhead) but widens the duplicate-execution
    /// window on worker crash. See <see cref="CompletionBatchSize"/> for the crash-safety trade-off.
    /// </para>
    /// </summary>
    public TimeSpan CompletionFlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// TTL applied to <c>BackgroundServiceLease</c> rows when a singleton service acquires
    /// the cluster lease. <c>null</c> falls back to 30 seconds. The lease must be renewed
    /// every <see cref="HealthCheckInterval"/> by the <c>Heartbeat</c> server task; the TTL
    /// should be at least 3× the heartbeat cadence to tolerate transient DB blips.
    /// </summary>
    public TimeSpan? BackgroundServiceLeaseTtl { get; set; }

    /// <summary>
    /// How long the supervisor waits between <c>TryAcquireAsync</c> attempts when a singleton
    /// service finds its lease held by another server. <c>null</c> falls back to 15 seconds.
    /// </summary>
    public TimeSpan? BackgroundServiceAcquirePollInterval { get; set; }

    internal List<WorkerGroupConfiguration> ExplicitWorkerGroups { get; } = [];

    /// <summary>
    /// Adds a worker group with its own worker count, queues, and polling interval.
    /// Top-level WorkerCount/Queues/PollingInterval become an additional implicit group.
    /// </summary>
    public void AddWorkerGroup(Action<WorkerGroupConfiguration> configure)
    {
        var group = new WorkerGroupConfiguration();
        configure(group);
        ExplicitWorkerGroups.Add(group);
    }

    /// <summary>
    /// Returns all effective worker groups. Top-level settings always form the first group.
    /// Any groups added via <see cref="AddWorkerGroup"/> are appended after.
    /// </summary>
    internal List<WorkerGroupConfiguration> GetEffectiveWorkerGroups()
    {
        var groups = new List<WorkerGroupConfiguration>
        {
            new()
            {
                WorkerCount = WorkerCount,
                Queues = Queues,
                PollingInterval = PollingInterval,
                MaxPollingInterval = MaxPollingInterval,
                PollingIntervalFactor = PollingIntervalFactor,
            },
        };

        groups.AddRange(ExplicitWorkerGroups);
        return groups;
    }

    /// <summary>
    /// Total number of workers across all groups.
    /// </summary>
    internal int TotalWorkerCount => GetEffectiveWorkerGroups().Sum(g => g.WorkerCount);
}

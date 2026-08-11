using Warp.Core;
using Warp.Core.Webhooks;

namespace Warp.Worker;

public class WorkerGroupConfiguration
{
    public int WorkerCount { get; set; } = Math.Min(Environment.ProcessorCount * 5, 20);

    public string[] Queues { get; set; } = ["default"];

    /// <summary>
    /// Each time the worker polls for a job, it will wait for this interval before polling again.
    /// Also serves as the floor for exponential backoff when consecutive polls return no work.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Dispatcher mode only. How many jobs to claim BEYOND what idle workers can start right now.
    /// <para>
    /// Null (the default) means <see cref="WorkerCount"/> — the depth the dispatcher has always
    /// buffered. Prefetch is what makes dispatcher mode worth running: workers free up one at a
    /// time, so without it a claim degenerates to roughly one job per round trip and throughput
    /// falls below plain per-worker fetching.
    /// </para>
    /// <para>
    /// Zero means never claim speculatively — nothing waits in the buffer beyond the moment a worker
    /// picks it up (a worker finishing its completion-batch flush is briefly counted as free, so a job
    /// can sit buffered for that instant; the claim is bounded, not zero), at the cost of that
    /// throughput. Choose it when cross-server fairness matters more: prefetched jobs
    /// are Processing on THIS server, so another server with idle workers cannot take them, and
    /// they wait behind whatever the local workers are running. Higher values trade further the
    /// other way and suit short jobs on a single-server or evenly loaded deployment.
    /// </para>
    /// </summary>
    public int? PrefetchCount { get; set; }
}

public class WarpServerConfiguration : WarpConfiguration
{
    private static readonly int DefaultWorkerCount = Math.Min(Environment.ProcessorCount * 5, 20);

    /// <summary>
    /// How many worker instances should be created. Applies to the implicit default worker group.
    /// </summary>
    public int WorkerCount { get; set; } = DefaultWorkerCount;

    /// <summary>
    /// Whether this server runs the job worker (fetch/execute loop + job-orchestration server
    /// tasks). Default <c>true</c> — a server processes jobs. Set to <c>false</c> (or call
    /// <see cref="DisableWorker"/>) for a service-only server that runs only
    /// <see cref="Core.BackgroundServices.WarpBackgroundService"/> instances and the server
    /// infrastructure (heartbeat, cleanup). When <c>false</c>, no worker hosts or job-only server
    /// tasks are registered and no <c>Worker</c>/<c>WorkerGroup</c> rows are created.
    /// </summary>
    public bool RunWorker { get; set; } = true;

    /// <summary>
    /// When <c>false</c> (the default), the Warp server context demotes EF Core's command-executed
    /// log to <c>Debug</c>, so the autonomous server loops (worker fetch, heartbeat, server tasks)
    /// don't flood the application's command logs at <c>Information</c>. Your own
    /// <c>DbContext</c>'s command logging is unaffected. Set to <c>true</c> to log the server
    /// context's commands at the normal level (e.g. when debugging Warp's own SQL).
    /// </summary>
    public bool EnableServerCommandLogging { get; set; }

    /// <summary>
    /// Each time the worker polls for a job, it will wait for this interval before polling again.
    /// Applies to the implicit default worker group. Also serves as the floor for exponential
    /// backoff when consecutive polls return no work.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(10);

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
    /// Dispatcher mode only. How many jobs to claim beyond what idle workers can start right now.
    /// Null (the default) means <see cref="WorkerCount"/>. Applies to the implicit default worker
    /// group; see <see cref="WorkerGroupConfiguration.PrefetchCount"/>.
    /// </summary>
    public int? PrefetchCount { get; set; }

    private string[] _queues = ["default"];
    private bool _queuesExplicitlySet;

    /// <summary>
    /// Queues this worker subscribes to. Applies to the implicit default worker group. When left
    /// untouched, the implicit group follows <see cref="WarpConfiguration.DefaultQueue"/> — the queue
    /// untargeted publishes actually land on — rather than the literal <c>"default"</c>. Setting this
    /// explicitly is an explicit decision and wins outright; see
    /// <see cref="GetEffectiveWorkerGroups"/> for why the tracking exists.
    /// </summary>
    public string[] Queues
    {
        get => _queues;
        set
        {
            // ConfigurationBinder invokes every public setter with a rebound copy of the CURRENT value
            // even when the bound section carries no matching key at all — so "the setter ran" cannot
            // mean "the user chose". A value indistinguishable from the untouched default is treated as
            // no choice; anything else is explicit. (The one intent this folds away — explicitly wanting
            // the literal "default" queue WHILE routing publishes elsewhere via DefaultQueue — is a
            // config that strands every untargeted publish, which is the bug this exists to prevent.)
            var isExplicit = _queuesExplicitlySet
                || value is null
                || !value.SequenceEqual(_queues, StringComparer.Ordinal);
            _queues = value!;
            _queuesExplicitlySet = isExplicit;
        }
    }

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
    /// <para>
    /// Raised 3s -> 5s: Heartbeat and ScheduledJobActivation were measured as 51% of an idle server's
    /// DB traffic. The binding constraint is <c>BackgroundServiceLease</c> renewal — this must tick
    /// comfortably inside the 30s lease TTL, and 5s still leaves a 6x margin. The visible cost is that
    /// pause propagation (§6.8) is now bounded by 5s rather than 3s.
    /// </para>
    /// </summary>
    public TimeSpan? HealthCheckInterval { get; set; } = TimeSpan.FromSeconds(5);

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
    /// How often <c>StatisticRollup</c> downsamples time-bucketed <c>Statistic</c> rows — fine (5-min) → hourly
    /// → daily — and deletes buckets past <see cref="WarpConfiguration.DailyStatisticsRetention"/> (§8.30). Off
    /// the hot path; replaces the old delete-only hourly prune. <b>The rollup is now the ONLY pruner of
    /// time-bucketed <c>Statistic</c> rows</b>, so setting this to <c>null</c> disables all such pruning and the
    /// fine/hourly/daily buckets accumulate unbounded — only do so if you prune the <c>Statistic</c> table by
    /// other means. Default 10 minutes.
    /// </summary>
    public TimeSpan? StatisticRollupInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often <c>BacklogSampler</c> samples per-queue backlog depth + oldest-job
    /// age (§8.26) — one grouped read of Enqueued jobs, off the worker hot path. Feeds the
    /// <c>warp.job.queue.depth</c> / <c>oldest_age_seconds</c> gauges and (under a DB-writing
    /// <see cref="WarpConfiguration.JobMetricsSink"/>) the per-queue backlog <c>Statistic</c> the dashboard
    /// reads. Set to <c>null</c> to disable.
    /// </summary>
    public TimeSpan? BacklogSampleInterval { get; set; } = TimeSpan.FromSeconds(60);

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
    public TimeSpan? ExpirationCleanupInterval { get; set; } = TimeSpan.FromMinutes(5);

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
    /// The out-of-the-box <see cref="MessageRoutingInterval"/>. Exposed so <c>UseDatabasePush()</c> can
    /// detect "still at the default" without hardcoding the number — the two drifted apart when the
    /// default moved from 1s to 10s, silently disabling the push backstop bump.
    /// </summary>
    public static readonly TimeSpan DefaultMessageRoutingInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the <see cref="Services.MessageRouter{TContext}"/> task runs to discover
    /// handlers for newly-enqueued <c>Kind=Message</c> rows. Set to <c>null</c> to disable
    /// the periodic auto-loop — routing is then driven entirely by <c>MessageEnqueued</c>
    /// push signals plus any explicit ticks.
    /// </summary>
    public TimeSpan? MessageRoutingInterval { get; set; } = DefaultMessageRoutingInterval;

    /// <summary>
    /// How often the scheduled-job activation task checks for rows in <see cref="Core.Enums.State.Scheduled"/>
    /// whose <c>ScheduleTime</c> has elapsed and flips them to <see cref="Core.Enums.State.Enqueued"/>.
    /// <para>
    /// Raised 5s -> 10s. This interval IS the worst-case latency between a job's <c>ScheduleTime</c> and
    /// it becoming eligible for pickup (§2.8) — the task is time-driven and deliberately does not
    /// participate in DB-push wake-up. It was the single largest contributor to idle DB traffic (28% of
    /// commands on the dispatcher-push config), and unlike Heartbeat it has no correctness coupling: the
    /// only cost is latency. Note rate-limit Wait-mode reschedules are floored by this value too (§8.8).
    /// </para>
    /// </summary>
    public TimeSpan ScheduledActivationInterval { get; set; } = TimeSpan.FromSeconds(10);

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
    /// The effective <see cref="BackgroundServiceLeaseTtl"/> when none is configured. Shared by every
    /// consumer that coalesces the null — the lease host, both providers' SQL factories, and the
    /// <c>AddWarpServer</c> renewal-margin validation. The validation is WHY this is one constant: it has
    /// to reject a heartbeat cadence the default TTL cannot survive, and a literal duplicated four ways
    /// would let the checked value drift from the running one.
    /// </summary>
    public static readonly TimeSpan DefaultBackgroundServiceLeaseTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// TTL applied to <c>BackgroundServiceLease</c> rows when a singleton service acquires
    /// the cluster lease. <c>null</c> falls back to <see cref="DefaultBackgroundServiceLeaseTtl"/>
    /// (30 seconds). The lease must be renewed every <see cref="HealthCheckInterval"/> by the
    /// <c>Heartbeat</c> server task; the TTL must be at least 3× the heartbeat cadence to tolerate
    /// transient DB blips — <c>AddWarpServer</c> enforces this at startup.
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
    internal List<WorkerGroupConfiguration> GetEffectiveWorkerGroups(string? resolvedDefaultQueue = null)
    {
        // An untouched Queues follows DefaultQueue. The Publisher stamps untargeted jobs with
        // WarpConfiguration.DefaultQueue, so a config that sets ONLY DefaultQueue = "orders" publishes
        // every job onto "orders" — and a group still polling the literal "default" would leave all of
        // them Enqueued forever with no error anywhere. An explicitly set Queues is an explicit decision
        // (a worker deliberately dedicated to other queues is a supported shape) and is never widened.
        //
        // resolvedDefaultQueue exists because this builder is not always the WarpConfiguration publishes
        // resolve: when AddWarp was called first, ITS builder wins the IOptions<WarpConfiguration>
        // TryAddSingleton, so publishes use AddWarp's DefaultQueue while this server builder's copy still
        // reads "default". The caller that knows the resolved options (WarpServerRegistration) passes that
        // value so the substitution follows the queue publishes actually land on.
        var baseQueues = _queuesExplicitlySet ? Queues : [resolvedDefaultQueue ?? DefaultQueue];

        // Webhook delivery is a Core feature (§8.20): the implicit default group always subscribes to the
        // dedicated warp:webhooks queue so any server with a worker drains deliveries — no per-process opt-in
        // to forget. Deduped so an explicit Queues that already lists it stays single.
        var defaultQueues = baseQueues.Contains(WebhookConstants.Queue, StringComparer.Ordinal)
            ? baseQueues
            : [.. baseQueues, WebhookConstants.Queue];

        var groups = new List<WorkerGroupConfiguration>
        {
            new()
            {
                WorkerCount = WorkerCount,
                Queues = defaultQueues,
                PollingInterval = PollingInterval,
                MaxPollingInterval = MaxPollingInterval,
                PollingIntervalFactor = PollingIntervalFactor,
                PrefetchCount = PrefetchCount,
            },
        };

        groups.AddRange(ExplicitWorkerGroups);
        return groups;
    }

    /// <summary>
    /// Turns this into a service-only server: the job worker does not run. Sets
    /// <see cref="RunWorker"/> to <c>false</c>, which is the single source of truth — no worker
    /// hosts, no job-only server tasks, and no <c>Worker</c>/<c>WorkerGroup</c> rows are created
    /// (<see cref="WorkerCount"/> is ignored while <see cref="RunWorker"/> is <c>false</c>). The
    /// server still registers itself and runs <c>Heartbeat</c>/<c>ServerCleanup</c>/
    /// <c>ExpirationCleanup</c> and any registered <see cref="Core.BackgroundServices.WarpBackgroundService"/>.
    /// </summary>
    public void DisableWorker()
    {
        RunWorker = false;
    }

    /// <summary>
    /// Total number of workers across all groups.
    /// </summary>
    internal int TotalWorkerCount => GetEffectiveWorkerGroups().Sum(g => g.WorkerCount);
}

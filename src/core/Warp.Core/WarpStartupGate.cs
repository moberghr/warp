using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data;

namespace Warp.Core;

/// <summary>
/// Startup coordination for <see cref="WarpConfiguration.RequireDatabaseAtStartup"/>, shared by Warp's packages
/// (the server, the providers' checks, the application-instance registration). Applications do not call it.
/// <para>
/// It runs a startup step that needs the database. With <c>RequireDatabaseAtStartup = true</c> (the default)
/// that is all: a failure throws, as before. With <c>false</c>, a step that fails because the database is
/// unreachable is retried in the background with backoff instead (checks first, then the steps after them),
/// the first such failure in the process logs one Warning and the rest Debug.
/// </para>
/// <para>
/// It is also the gate the server's loops wait on: open from the start in a process without a Warp server,
/// opened by the server registration once its rows are written. With the default setting registration
/// finishes inside <c>StartAsync</c>, so every loop finds it open and starts exactly as before.
/// </para>
/// </summary>
public sealed class WarpStartupGate : IDisposable
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _lock = new();
    private readonly List<Task<bool>> _deferred = [];
    private readonly List<Task<bool>> _checks = [];
    private readonly bool _requireDatabase;
    private readonly TimeProvider _time;
    private readonly IDatabaseExceptionClassifier? _classifier;
    private int _warned;

    /// <summary>Initializes a new instance of the <see cref="WarpStartupGate"/> class. Registered by <c>AddWarp</c>.</summary>
    /// <param name="configuration">Warp's configuration (<see cref="WarpConfiguration.RequireDatabaseAtStartup"/>).</param>
    /// <param name="time">The clock the retry backoff waits on.</param>
    /// <param name="servers">Present in a server process: the gate then waits for the server registration.</param>
    /// <param name="classifier">The provider's classifier (<see cref="IDatabaseExceptionClassifier.IsConnectionFailure"/>), when one is registered.</param>
    public WarpStartupGate(IOptions<WarpConfiguration> configuration, TimeProvider time, IEnumerable<IWarpServerPresence> servers, IDatabaseExceptionClassifier? classifier = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(servers);

        _requireDatabase = configuration.Value.RequireDatabaseAtStartup;
        _time = time;
        _classifier = classifier;
        if (!servers.Any())
        {
            _opened.TrySetResult();
        }
    }

    /// <summary>Whether the server is registered (always true in a process without a Warp server).</summary>
    public bool IsOpen => _opened.Task.IsCompletedSuccessfully;

    // Test seam: released each time a background retry starts waiting out its backoff, after the wait is set up.
    internal SemaphoreSlim? RetryWaiting { get; set; }

    /// <summary>Completes when the server is registered, or throws when <paramref name="cancellationToken"/> is cancelled first.</summary>
    /// <param name="cancellationToken">Cancels the wait (the host stopping).</param>
    /// <returns>A task that completes when the gate opens.</returns>
    public Task WaitUntilOpenAsync(CancellationToken cancellationToken) => _opened.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Runs a startup check other steps depend on (a provider's server-version check). Throws as before, unless
    /// <see cref="WarpConfiguration.RequireDatabaseAtStartup"/> is off and the database is unreachable: then the
    /// check is retried in the background and the server registration waits for it to pass.
    /// </summary>
    /// <param name="step">What the step is, for the log ("PostgreSQL version check").</param>
    /// <param name="attempt">The step. Called again, with the gate's stopping token, on each retry.</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="cancellationToken">The startup token.</param>
    /// <param name="probe">With the opt-out only: run before <paramref name="attempt"/>, which runs once it succeeds.
    /// <see cref="ProbeConnectionAsync"/> opens a bare connection, so an unreachable database is not also logged at
    /// Error by EF Core on every retry.</param>
    /// <returns><c>true</c> when the step ran now; <c>false</c> when it was deferred.</returns>
    public Task<bool> RunCheckAsync(string step, Func<CancellationToken, Task> attempt, ILogger logger, CancellationToken cancellationToken, Func<CancellationToken, Task>? probe = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(logger);

        return RunAsync(new Step(step, attempt, probe, logger, IsCheck: true), cancellationToken);
    }

    /// <summary>
    /// Runs a startup step after the checks (the server registration). As <see cref="RunCheckAsync"/>, and it
    /// is not attempted while a deferred check has not passed: it is deferred behind it.
    /// </summary>
    /// <param name="step">What the step is, for the log ("server registration").</param>
    /// <param name="attempt">The step. Called again, with the gate's stopping token, on each retry.</param>
    /// <param name="logger">The caller's logger.</param>
    /// <param name="cancellationToken">The startup token.</param>
    /// <param name="probe">As in <see cref="RunCheckAsync"/>.</param>
    /// <returns><c>true</c> when the step ran now; <c>false</c> when it was deferred.</returns>
    public Task<bool> RunAfterChecksAsync(string step, Func<CancellationToken, Task> attempt, ILogger logger, CancellationToken cancellationToken, Func<CancellationToken, Task>? probe = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(logger);

        return RunAsync(new Step(step, attempt, probe, logger, IsCheck: false), cancellationToken);
    }

    /// <summary>
    /// Opens and closes <paramref name="context"/>'s connection as a bare ADO.NET connection: throws when the
    /// database cannot be reached, without EF Core's connection-error logging. A no-op for a non-relational
    /// provider or an already open connection.
    /// </summary>
    /// <param name="context">The context whose database is probed.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>A task that completes when the connection opened and closed.</returns>
    public static async Task ProbeConnectionAsync(DbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Database.IsRelational())
        {
            return;
        }

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Closed)
        {
            return;
        }

        await connection.OpenAsync(cancellationToken);
        await connection.CloseAsync();
    }

    /// <summary>Stops every background retry and waits for them. Idempotent; called by each step's <c>StopAsync</c>.</summary>
    /// <returns>A task that completes when no retry is running.</returns>
    public async Task StopAsync()
    {
        Task<bool>[] deferred;
        lock (_lock)
        {
            deferred = [.. _deferred];
        }

        if (!_stopping.IsCancellationRequested)
        {
            await _stopping.CancelAsync();
        }

        await Task.WhenAll(deferred);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    // Opened by the server registration once its rows are written (Warp.Worker).
    internal void Open() => _opened.TrySetResult();

    // For a loop's first line: false when the host stopped before the server registered (the loop then returns).
    // A null gate (a host built by hand) counts as open.
    internal static async Task<bool> WaitForServerAsync(WarpStartupGate? gate, CancellationToken stopping)
    {
        if (gate is null || gate.IsOpen)
        {
            return true;
        }

        try
        {
            await gate.WaitUntilOpenAsync(stopping);

            return true;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return false;
        }
    }

    // A failure that means the database could not be reached, as opposed to one it reported: what the provider's
    // classifier says (SQL Server decides by error number, it has no SQLSTATE), a socket or a timeout, a provider
    // exception the provider marks transient, or a SQLSTATE of connection exception (08), insufficient resources
    // (53), operator intervention (57P: shutting down, cannot connect now) or a database that does not exist yet
    // (3D000, a container still initialising). Authentication and permission errors are not: they do not fix
    // themselves, so they fail as before.
    internal bool IsDatabaseUnavailable(Exception exception) =>
        _classifier?.IsConnectionFailure(exception) == true || IsUnreachableByDefault(exception);

    internal static bool IsUnreachableByDefault(Exception exception)
    {
        // A socket or timeout anywhere in the chain first: a provider exception wrapping one says nothing on its own
        // (SqlClient on Linux reports a refused port as SqlException, provider error 35, around a SocketException).
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException or TimeoutException)
            {
                return true;
            }
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException db)
            {
                return db.IsTransient
                    || (db.SqlState is { } state
                        && (state.StartsWith("08", StringComparison.Ordinal)
                            || state.StartsWith("53", StringComparison.Ordinal)
                            || state.StartsWith("57P", StringComparison.Ordinal)
                            || string.Equals(state, "3D000", StringComparison.Ordinal)));
            }
        }

        return false;
    }

    private async Task<bool> RunAsync(Step step, CancellationToken cancellationToken)
    {
        if (!step.IsCheck && ChecksPending())
        {
            step.Logger.LogDebug("Warp startup step {Step} waits for a startup check that is retrying in the background.", step.Name);
            Defer(step);

            return false;
        }

        try
        {
            if (!_requireDatabase && step.Probe is not null)
            {
                await step.Probe(cancellationToken);
            }

            await step.Attempt(cancellationToken);

            return true;
        }
        catch (Exception ex) when (!_requireDatabase && !cancellationToken.IsCancellationRequested && IsDatabaseUnavailable(ex))
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                step.Logger.LogWarning(ex, "Warp could not reach the database at startup ({Step}); retrying in the background. Nothing in Warp that needs the database runs until it is reachable.", step.Name);
            }
            else
            {
                step.Logger.LogDebug(ex, "Warp could not reach the database at startup ({Step}); retrying in the background.", step.Name);
            }

            Defer(step);

            return false;
        }
    }

    private bool ChecksPending()
    {
        lock (_lock)
        {
            return _checks.Exists(x => !x.IsCompletedSuccessfully || !x.Result);
        }
    }

    private void Defer(Step step)
    {
        lock (_lock)
        {
            Task<bool>[] checks = step.IsCheck ? [] : [.. _checks];
            var retry = Task.Run(() => RetryAsync(step, checks, _stopping.Token));
            _deferred.Add(retry);
            if (step.IsCheck)
            {
                _checks.Add(retry);
            }
        }
    }

    // Never throws: the outcome is the result, and every failure is logged here.
    private async Task<bool> RetryAsync(Step step, Task<bool>[] checks, CancellationToken stopping)
    {
        try
        {
            var passed = await Task.WhenAll(checks).WaitAsync(stopping);

            // A check stopped by shutdown also reports false: that is not a failure.
            if (stopping.IsCancellationRequested)
            {
                return false;
            }

            if (passed.Contains(false))
            {
                step.Logger.LogError("Warp startup step {Step} is not run: a startup check it waits for failed (logged above). It is not retried in this process.", step.Name);

                return false;
            }

            var delay = InitialRetryDelay;
            while (true)
            {
                var wait = Task.Delay(delay, _time, stopping);
                RetryWaiting?.Release();
                await wait;

                try
                {
                    if (step.Probe is not null)
                    {
                        await step.Probe(stopping);
                    }

                    await step.Attempt(stopping);
                    step.Logger.LogInformation("Warp reached the database: startup step {Step} succeeded.", step.Name);

                    return true;
                }
                catch (Exception ex) when (!stopping.IsCancellationRequested && IsDatabaseUnavailable(ex))
                {
                    delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
                    step.Logger.LogDebug(ex, "Warp still cannot reach the database ({Step}); retrying in {Delay}.", step.Name, delay);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            step.Logger.LogError(ex, "Warp startup step {Step} failed once the database was reachable. It is not retried in this process; what waits for it (for the server registration: the workers, server tasks and background services) does not start.", step.Name);

            return false;
        }
    }

    private sealed record Step(string Name, Func<CancellationToken, Task> Attempt, Func<CancellationToken, Task>? Probe, ILogger Logger, bool IsCheck);
}

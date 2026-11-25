using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO.MemoryMappedFiles;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Counts database round trips on the CLIENT side, in the benchmark process, for
/// <see cref="DatabaseStatementsDiagnoser"/> to read from the host.
/// <para>
/// Counted at the driver, not with EF interceptors, so nothing Warp sends escapes it: EF commands on the
/// application's context and on Warp's server context, and the raw ADO.NET the providers issue outside EF
/// (the set-based completion, the heartbeat and cluster-task statements, Medallion's lock calls, the
/// notification transports). PostgreSQL: every measurement of Npgsql's <c>db.client.operation.duration</c>
/// histogram, which Npgsql records once per command or batch sent. SQL Server: every
/// <c>WriteCommandAfter</c> or <c>WriteCommandError</c> event of SqlClient's <c>SqlClientDiagnosticListener</c>,
/// one per command executed. A batch is one round trip on both.
/// </para>
/// <para>
/// What is counted is COMMANDS, not every round trip: transaction control is not a command to either driver
/// (checked: a BEGIN, a SELECT and a COMMIT count one on both, Npgsql sending BEGIN with the first command and
/// COMMIT outside its command path, SqlClient committing through its transaction manager).
/// </para>
/// <para>
/// Commands sent inside a <see cref="Harness.Begin"/> scope are counted separately, so the harness's own
/// polling and cleanup are not reported as Warp's.
/// </para>
/// <para>
/// BenchmarkDotNet runs the benchmark in a child process and the diagnoser in the host, so the counts cross
/// the process boundary through a memory-mapped file: the diagnoser names it in
/// <see cref="FileVariable"/> before the child starts (the child inherits the environment), the child keeps
/// running totals in it (<see cref="Slot"/>), and the host reads them at the start and end of the measured
/// window. Without the variable (a lab run, a test) nothing is counted.
/// </para>
/// </summary>
public static class RoundTripCounter
{
    public const string FileVariable = "WARP_BENCH_ROUNDTRIP_FILE";

    /// <summary>The file's size: one eight-byte running total per <see cref="Slot"/>.</summary>
    public const int FileSize = 4 * sizeof(long);

    private static readonly Lock Gate = new();
    private static readonly long[] Totals = new long[4];
    private static MemoryMappedViewAccessor? _view;
    private static MeterListener? _meters;
    private static IDisposable? _listeners;

    /// <summary>The running totals the file holds, at eight bytes each.</summary>
    public enum Slot
    {
        /// <summary>Commands Warp sent.</summary>
        Commands = 0,

        /// <summary>Commands the harness sent (inside a <see cref="Harness.Begin"/> scope).</summary>
        HarnessCommands = 1,

        /// <summary>Jobs completed by the first of two servers (by server id), counted by the benchmark.</summary>
        FirstServerJobs = 2,

        /// <summary>Jobs completed by the second of two servers.</summary>
        SecondServerJobs = 3,
    }

    /// <summary>Starts counting in this process, once. Called by the fixture before its hosts start.</summary>
    public static void Start()
    {
        var path = Environment.GetEnvironmentVariable(FileVariable);

        lock (Gate)
        {
            if (path is null || _meters is not null)
            {
                return;
            }

            // Created and zeroed once per process; a restart after Stop keeps counting from where it was.
            if (_view is null)
            {
                var file = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, FileSize, MemoryMappedFileAccess.ReadWrite);
                _view = file.CreateViewAccessor(0, FileSize);
                for (var i = 0; i < Totals.Length; i++)
                {
                    _view.Write(i * sizeof(long), 0L);
                }
            }

            _meters = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (string.Equals(instrument.Meter.Name, "Npgsql", StringComparison.Ordinal)
                        && string.Equals(instrument.Name, "db.client.operation.duration", StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _meters.SetMeasurementEventCallback<double>((_, _, _, _) => Increment());
            _meters.Start();

            _listeners = DiagnosticListener.AllListeners.Subscribe(new SqlClientObserver());
        }
    }

    /// <summary>Stops counting. Called by the fixture when it is disposed.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _meters?.Dispose();
            _meters = null;
            _listeners?.Dispose();
            _listeners = null;
        }
    }

    /// <summary>Adds to one of the totals the benchmark itself keeps (the two servers' completed jobs).</summary>
    public static void Add(Slot slot, long amount) => Write(slot, Interlocked.Add(ref Totals[(int)slot], amount));

    // Inside a harness scope the command is the harness's. The flag is async-local and both drivers raise their
    // event on the flow that sent the command, so it reads the sender's scope.
    private static void Increment()
    {
        var slot = Harness.IsActive ? Slot.HarnessCommands : Slot.Commands;
        Write(slot, Interlocked.Increment(ref Totals[(int)slot]));
    }

    // Several threads may write out of order; the host reads only once the window has gone quiet (it holds the
    // benchmark at each signal), when the last write is the total.
    private static void Write(Slot slot, long value) => _view?.Write((int)slot * sizeof(long), value);

    private sealed class SqlClientObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value)
        {
            if (string.Equals(value.Name, "SqlClientDiagnosticListener", StringComparison.Ordinal))
            {
                value.Subscribe(new CommandObserver(), x =>
                    x.EndsWith("WriteCommandAfter", StringComparison.Ordinal) || x.EndsWith("WriteCommandError", StringComparison.Ordinal));
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }

    private sealed class CommandObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) || value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
            {
                Increment();
            }
        }

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}

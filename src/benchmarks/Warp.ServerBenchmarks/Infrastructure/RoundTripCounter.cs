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
/// BenchmarkDotNet runs the benchmark in a child process and the diagnoser in the host, so the count crosses
/// the process boundary through a memory-mapped file: the diagnoser names it in
/// <see cref="FileVariable"/> before the child starts (the child inherits the environment), the child keeps
/// the running total in its first eight bytes, and the host reads them at the start and end of the measured
/// window. Without the variable (a lab run, a test) nothing is counted.
/// </para>
/// </summary>
public static class RoundTripCounter
{
    public const string FileVariable = "WARP_BENCH_ROUNDTRIP_FILE";

    private static readonly Lock Gate = new();
    private static long _count;
    private static MemoryMappedViewAccessor? _view;
    private static MeterListener? _meters;
    private static IDisposable? _listeners;

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
                var file = MemoryMappedFile.CreateFromFile(path, FileMode.OpenOrCreate, null, sizeof(long), MemoryMappedFileAccess.ReadWrite);
                _view = file.CreateViewAccessor(0, sizeof(long));
                _view.Write(0, 0L);
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

    private static void Increment()
    {
        var value = Interlocked.Increment(ref _count);

        // Several threads may write out of order; the host reads only once the window has gone quiet, when the
        // last write is the total.
        _view?.Write(0, value);
    }

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

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Warp.MatrixBench;

/// <summary>
/// Counts the warnings and errors Warp logs, and keeps the first few. Only categories under <c>Warp.</c>:
/// what the library reports about itself, not the host's or EF's chatter.
/// </summary>
public sealed class WarpLogCapture : ILoggerProvider
{
    private const int MaxSamples = 8;

    private readonly ConcurrentQueue<string> _samples = new();
    private int _warnings;
    private int _errors;
    private int _sampled;

    public int Warnings => Volatile.Read(ref _warnings);

    public int Errors => Volatile.Read(ref _errors);

    public IReadOnlyList<string> Samples => [.. _samples];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Reset()
    {
        _samples.Clear();
        Interlocked.Exchange(ref _warnings, 0);
        Interlocked.Exchange(ref _errors, 0);
        Interlocked.Exchange(ref _sampled, 0);
    }

    public void Dispose()
    {
    }

    private void Record(LogLevel level, string category, string message, Exception? exception)
    {
        if (level >= LogLevel.Error)
        {
            Interlocked.Increment(ref _errors);
        }
        else
        {
            Interlocked.Increment(ref _warnings);
        }

        if (Interlocked.Increment(ref _sampled) > MaxSamples)
        {
            return;
        }

        var text = $"{level} {category}: {message}";
        if (exception is not null)
        {
            text += $" [{exception.GetType().Name}: {exception.Message}]";
        }

        _samples.Enqueue(text.Length > 400 ? text[..400] : text);
    }

    private sealed class CaptureLogger : ILogger
    {
        private readonly WarpLogCapture _owner;
        private readonly string _category;
        private readonly bool _captured;

        public CaptureLogger(WarpLogCapture owner, string category)
        {
            _owner = owner;
            _category = category;
            _captured = category.StartsWith("Warp.", StringComparison.Ordinal)
                && !category.StartsWith("Warp.MatrixBench", StringComparison.Ordinal);
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _captured && logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _owner.Record(logLevel, _category, formatter(state, exception), exception);
        }
    }
}

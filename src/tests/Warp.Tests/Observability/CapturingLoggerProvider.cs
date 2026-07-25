using Microsoft.Extensions.Logging;

namespace Warp.Tests.Observability;

/// <summary>
/// Test <see cref="ILoggerProvider"/> that captures every emitted log — category, level, formatted message,
/// and the structured state flattened into a field dictionary — so the OTel call-log recorders can be
/// asserted on the fields they carry (each becomes an OTLP LogRecord attribute in production).
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly List<CapturedLog> _logs = [];

    public IReadOnlyList<CapturedLog> Logs
    {
        get
        {
            lock (_gate)
            {
                return [.. _logs];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void Dispose()
    {
    }

    private void Add(CapturedLog log)
    {
        lock (_gate)
        {
            _logs.Add(log);
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly CapturingLoggerProvider _owner;

        public CapturingLogger(string category, CapturingLoggerProvider owner)
        {
            _category = category;
            _owner = owner;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> structured)
            {
                foreach (var field in structured)
                {
                    fields[field.Key] = field.Value;
                }
            }

            _owner.Add(new CapturedLog(_category, logLevel, formatter(state, exception), fields));
        }
    }
}

/// <summary>A single captured log entry.</summary>
internal sealed record CapturedLog(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Fields);

/// <summary>
/// Minimal <see cref="ILoggerFactory"/> over a <see cref="CapturingLoggerProvider"/> — avoids the
/// disposal lifetime coupling of <c>LoggerFactory.Create</c> (which would dispose the captured loggers).
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly CapturingLoggerProvider _provider;

    public CapturingLoggerFactory(CapturingLoggerProvider provider) => _provider = provider;

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);

    public void Dispose()
    {
    }
}

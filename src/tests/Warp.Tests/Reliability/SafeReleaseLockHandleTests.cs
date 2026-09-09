using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Core;

namespace Warp.Tests.Reliability;

// Every DB-backed lock/semaphore provider wraps its handle in SafeReleaseLockHandle, so the
// Warp-wide contract is "acquiring can fail, releasing never throws". Medallion's Postgres
// release throws InvalidOperationException("Attempted to release a lock that was not held")
// when pg_advisory_unlock returns false — which happens whenever the lock session was lost,
// classically behind a transaction-mode pooler (Neon's '-pooler' endpoint, PgBouncer).
// Propagating that would fail already-committed work (AddOrUpdateRecurringJob at startup) or
// replace the guarded body's real exception (SagaHandlerProxy / ServerTaskLoop finally blocks).
[Trait("Category", "NoDb")]
public sealed class SafeReleaseLockHandleTests
{
    [TimedFact]
    public void Wrap_NullHandle_ReturnsNull()
    {
        // The "lock not acquired" signal must survive wrapping — every call site branches on it.
        var wrapped = SafeReleaseLockHandle.Wrap(null, "warp:recurring:x", NullLogger.Instance);

        wrapped.ShouldBeNull();
    }

    [TimedFact]
    public async Task DisposeAsync_InnerSucceeds_DisposesInnerOnce()
    {
        var inner = new CountingHandle();

        var wrapped = SafeReleaseLockHandle.Wrap(inner, "warp:recurring:x", NullLogger.Instance);
        await wrapped!.DisposeAsync();

        inner.DisposeCount.ShouldBe(1);
    }

    [TimedFact]
    public async Task DisposeAsync_InnerThrowsLockNotHeld_DoesNotThrowAndWarns()
    {
        var sink = new WarningSink();
        var inner = new ThrowingHandle(new InvalidOperationException("Attempted to release a lock that was not held"));

        var wrapped = SafeReleaseLockHandle.Wrap(inner, "warp:recurring:daily-report", new CapturingLogger(sink));
        await Should.NotThrowAsync(async () => await wrapped!.DisposeAsync());

        sink.Warnings.Count.ShouldBe(1);
        sink.Warnings[0].ShouldContain("warp:recurring:daily-report");
    }

    [TimedFact]
    public async Task DisposeAsync_InnerThrows_DoesNotMaskAnAmbientException()
    {
        // The regression that matters most: SagaHandlerProxy and ServerTaskLoop release in a
        // finally, where a throwing release REPLACES the body's exception with a lock error.
        var inner = new ThrowingHandle(new InvalidOperationException("Attempted to release a lock that was not held"));
        var wrapped = SafeReleaseLockHandle.Wrap(inner, "warp:saga:Order:42", NullLogger.Instance);

        var thrown = await Should.ThrowAsync<TimeoutException>(async () =>
        {
            try
            {
                throw new TimeoutException("handler blew up");
            }
            finally
            {
                await wrapped!.DisposeAsync();
            }
        });

        thrown.Message.ShouldBe("handler blew up");
    }

    private sealed class CountingHandle : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandle : IAsyncDisposable
    {
        private readonly Exception _exception;

        public ThrowingHandle(Exception exception) => _exception = exception;

        public ValueTask DisposeAsync() => throw _exception;
    }

    private sealed class WarningSink
    {
        public List<string> Warnings { get; } = [];
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly WarningSink _sink;

        public CapturingLogger(WarningSink sink) => _sink = sink;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                _sink.Warnings.Add(formatter(state, exception));
            }
        }
    }
}

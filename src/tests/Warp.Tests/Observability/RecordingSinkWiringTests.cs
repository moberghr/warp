using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Queries;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Observability;
using Warp.Http.Observability;

namespace Warp.Tests.Observability;

/// <summary>
/// DI-level coverage for the <see cref="RecordingSink"/> selection in <c>AddAdapters</c> /
/// <c>AddEndpointObservability</c>: <c>Database</c> wires the DB recorder + flusher (unchanged);
/// <c>Otel</c> wires the OTel recorder and NO flusher (no DB writes); <c>Both</c> wires a composite that
/// fans a record to both sinks. Built through the real <c>AddWarp</c> DI wiring (NoDb: InMemory context +
/// mocked provider scaffolding).
/// </summary>
[Trait("Category", "NoDb")]
public class RecordingSinkWiringTests
{
    [TimedFact]
    public void AddAdapters_DatabaseSink_WiresDbRecorderAndFlusher()
    {
        var (services, _) = BuildServices(opt => opt.AddAdapters());

        services.ShouldContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAdapterCallRecorder>().ShouldBeOfType<DbAdapterCallRecorder>();
    }

    [TimedFact]
    public void AddAdapters_OtelSink_WiresOtelRecorder_NoFlusher_NoDbRecorder()
    {
        var (services, _) = BuildServices(opt => opt.AddAdapters(o => o.Sink = RecordingSink.Otel));

        services.ShouldNotContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));
        services.ShouldNotContain(d => d.ServiceType == typeof(DbAdapterCallRecorder));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAdapterCallRecorder>().ShouldBeOfType<OtelAdapterCallRecorder>();

        // Recording marker still registered so the dashboard "adapters" flag reports true under Otel-only.
        sp.GetService<IAdapterRecordingMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddAdapters_BothSink_WiresComposite_ThatFansToBothSinks()
    {
        var (services, logs) = BuildServices(opt => opt.AddAdapters(o => o.Sink = RecordingSink.Both));

        services.ShouldContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        var recorder = sp.GetRequiredService<IAdapterCallRecorder>();
        recorder.ShouldBeOfType<CompositeAdapterCallRecorder>();

        recorder.Record(new AdapterCallRecord
        {
            AdapterName = "vendor",
            Operation = "GetOrders",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Attempts = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        }).ShouldBeTrue();

        // DB sink accepted it (the record sits in the bounded channel awaiting the flusher).
        sp.GetRequiredService<DbAdapterCallRecorder>().Reader.TryRead(out _).ShouldBeTrue();

        // OTel sink emitted the structured log.
        logs.Logs.ShouldContain(x => string.Equals(x.Category, "Warp.Adapters.CallLog", StringComparison.Ordinal));
    }

    [TimedFact]
    public void AddEndpointObservability_DatabaseSink_WiresDbRecorderAndFlusher()
    {
        var (services, _) = BuildServices(opt => opt.AddEndpointObservability());

        services.ShouldContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IEndpointCallRecorder>().ShouldBeOfType<DbEndpointCallRecorder>();
    }

    [TimedFact]
    public void AddEndpointObservability_OtelSink_WiresOtelRecorder_NoFlusher_NoDbRecorder()
    {
        var (services, _) = BuildServices(opt => opt.AddEndpointObservability(o => o.Sink = RecordingSink.Otel));

        services.ShouldNotContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));
        services.ShouldNotContain(d => d.ServiceType == typeof(DbEndpointCallRecorder));

        using var sp = services.BuildServiceProvider();

        // IEndpointCallRecorder is present (OTel recorder) — the addons "endpoints" flag keys on its presence.
        sp.GetRequiredService<IEndpointCallRecorder>().ShouldBeOfType<OtelEndpointCallRecorder>();
    }

    [TimedFact]
    public void AddEndpointObservability_BothSink_WiresComposite_ThatFansToBothSinks()
    {
        var (services, logs) = BuildServices(opt => opt.AddEndpointObservability(o => o.Sink = RecordingSink.Both));

        services.ShouldContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        var recorder = sp.GetRequiredService<IEndpointCallRecorder>();
        recorder.ShouldBeOfType<CompositeEndpointCallRecorder>();

        recorder.Record(new EndpointCallRecord
        {
            Method = "GET",
            RouteTemplate = "/things",
            Operation = "GET /things",
            Timestamp = DateTime.UtcNow,
            DurationMs = 1,
            Outcome = AdapterCallOutcome.Success,
            MachineName = "test-host",
        }).ShouldBeTrue();

        sp.GetRequiredService<DbEndpointCallRecorder>().Reader.TryRead(out _).ShouldBeTrue();
        logs.Logs.ShouldContain(x => string.Equals(x.Category, "Warp.Endpoints.CallLog", StringComparison.Ordinal));
    }

    private static (IServiceCollection Services, CapturingLoggerProvider Logs) BuildServices(Action<WarpBuilder<TestContext>> configure)
    {
        var services = new ServiceCollection();
        var logs = new CapturingLoggerProvider();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton<ILoggerProvider>(logs);
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"sink-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddWarp<TestContext>(configure);

        return (services, logs);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.ClientObservability;
using Warp.Core.Data.Queries;
using Warp.Core.Endpoints;
using Warp.Core.Observability;
using Warp.Http.Observability;

namespace Warp.Tests.Observability;

/// <summary>
/// DI-level coverage for the <see cref="RecordingSink"/> selection in <c>AddAdapters</c> /
/// <c>AddEndpointObservability</c>: <c>Database</c> wires the DB recorder + flusher (unchanged);
/// <c>Otel</c> wires NEITHER (the captured detail rides the span, §8.24) — the adapter recorder falls back to
/// the null recorder and no endpoint recorder is registered, while the presence markers still register so the
/// dashboard nav flags stay true; <c>Both</c> keeps the DB recorder + flusher AND enriches the span. Built
/// through the real <c>AddWarp</c> DI wiring (NoDb: InMemory context + mocked provider scaffolding).
/// </summary>
[Trait("Category", "NoDb")]
public class RecordingSinkWiringTests
{
    [TimedFact]
    public void AddAdapters_DatabaseSink_WiresDbRecorderAndFlusher_NoSpanEnrichment()
    {
        var services = BuildServices(opt => opt.AddAdapters());

        services.ShouldContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAdapterCallRecorder>().ShouldBeOfType<DbAdapterCallRecorder>();
        sp.GetRequiredService<AdapterRecordingSettings>().EnrichSpanDetail.ShouldBeFalse();
    }

    [TimedFact]
    public void AddAdapters_OtelSink_NoFlusher_NoDbRecorder_FallsBackToNullRecorder_EnrichesSpan()
    {
        var services = BuildServices(opt => opt.AddAdapters(o => o.Sink = RecordingSink.Otel));

        services.ShouldNotContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));
        services.ShouldNotContain(d => d.ServiceType == typeof(DbAdapterCallRecorder));

        using var sp = services.BuildServiceProvider();

        // No DB recorder registered ⇒ the Core fallback wins; the detail rides the span instead.
        sp.GetRequiredService<IAdapterCallRecorder>().ShouldBeOfType<NullAdapterCallRecorder>();
        sp.GetRequiredService<AdapterRecordingSettings>().EnrichSpanDetail.ShouldBeTrue();

        // Recording marker still registered so the dashboard "adapters" flag reports true under Otel-only.
        sp.GetService<IAdapterRecordingMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddAdapters_BothSink_KeepsDbRecorderAndFlusher_AndEnrichesSpan()
    {
        var services = BuildServices(opt => opt.AddAdapters(o => o.Sink = RecordingSink.Both));

        services.ShouldContain(d => d.ImplementationType == typeof(AdapterCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IAdapterCallRecorder>().ShouldBeOfType<DbAdapterCallRecorder>();
        sp.GetRequiredService<AdapterRecordingSettings>().EnrichSpanDetail.ShouldBeTrue();
    }

    [TimedFact]
    public void AddEndpointObservability_DatabaseSink_WiresDbRecorderAndFlusher()
    {
        var services = BuildServices(opt => opt.AddEndpointObservability());

        services.ShouldContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IEndpointCallRecorder>().ShouldBeOfType<DbEndpointCallRecorder>();
        sp.GetService<IEndpointObservabilityMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddEndpointObservability_OtelSink_NoFlusher_NoRecorder_MarkerStillPresent()
    {
        var services = BuildServices(opt => opt.AddEndpointObservability(o => o.Sink = RecordingSink.Otel));

        services.ShouldNotContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));
        services.ShouldNotContain(d => d.ServiceType == typeof(DbEndpointCallRecorder));

        using var sp = services.BuildServiceProvider();

        // No recorder under Otel-only (the middleware enriches the request span); the addons "endpoints"
        // flag keys on the marker, which is registered regardless of sink.
        sp.GetService<IEndpointCallRecorder>().ShouldBeNull();
        sp.GetService<IEndpointObservabilityMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddEndpointObservability_BothSink_KeepsDbRecorderAndFlusher()
    {
        var services = BuildServices(opt => opt.AddEndpointObservability(o => o.Sink = RecordingSink.Both));

        services.ShouldContain(d => d.ImplementationType == typeof(EndpointCallFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IEndpointCallRecorder>().ShouldBeOfType<DbEndpointCallRecorder>();
        sp.GetService<IEndpointObservabilityMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddClientObservability_DatabaseSink_WiresRecorderFlusherCardinalityAndMarker()
    {
        var services = BuildServices(opt => opt.AddClientObservability(o => o.AddIngestKey("app", "pk")));

        services.ShouldContain(d => d.ImplementationType == typeof(ClientEventFlusher<TestContext>));

        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<IClientEventRecorder>().ShouldBeOfType<DbClientEventRecorder>();
        sp.GetService<ClientEventCardinality>().ShouldNotBeNull();
        sp.GetService<IClientObservabilityMarker>().ShouldNotBeNull();
    }

    [TimedFact]
    public void AddClientObservability_OtelSink_NoRecorderNoFlusher_MarkerStillPresent()
    {
        var services = BuildServices(opt => opt.AddClientObservability(o =>
        {
            o.Sink = RecordingSink.Otel;
            o.AddIngestKey("app", "pk");
        }));

        services.ShouldNotContain(d => d.ImplementationType == typeof(ClientEventFlusher<TestContext>));
        services.ShouldNotContain(d => d.ServiceType == typeof(DbClientEventRecorder));

        using var sp = services.BuildServiceProvider();

        // No recorder registered under Otel-only; the meters carry the data. Marker still present so the
        // dashboard "client" nav flag stays true.
        sp.GetService<IClientEventRecorder>().ShouldBeNull();
        sp.GetService<IClientObservabilityMarker>().ShouldNotBeNull();

        // The rate limiter is registered regardless of sink (it guards the public endpoint, not the DB write).
        sp.GetService<ClientIngestRateLimiter>().ShouldNotBeNull();
    }

    private static ServiceCollection BuildServices(Action<WarpBuilder<TestContext>> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace));
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"sink-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddWarp<TestContext>(configure);

        return services;
    }
}

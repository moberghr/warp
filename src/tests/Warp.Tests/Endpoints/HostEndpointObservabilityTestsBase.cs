using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Endpoints;
using Warp.Core.Enums;
using Warp.Core.Logging;
using Warp.Core.Observability;
using Warp.Dashboard;
using Warp.Http;
using Warp.Http.Observability;
using Warp.Tests.EndToEnd;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.Services;

namespace Warp.Tests.Endpoints;

/// <summary>
/// The host's own endpoints — real MVC controllers and minimal APIs — once opted in
/// (<c>WithWarpObservability()</c> / <c>[WarpObserved]</c>) go through the SAME path as a Warp endpoint: the real
/// middleware hands the record to the real <see cref="DbEndpointCallRecorder"/>, the test drains that channel
/// (awaiting it — no polling) and persists through <c>EndpointCallFlusher.PersistBatchAsync</c> (the flusher's
/// own write, §4.8 — its background loop is removed so it cannot race the drain), and a 5xx becomes an Issue
/// through <see cref="ErrorGroupAggregator{TContext}"/>. Endpoints nobody opted in, and the dashboard, are
/// never recorded: every negative case sends an opted-in sentinel request afterwards and asserts it is the only
/// record in the channel.
/// </summary>
[GenerateDatabaseTests]
public abstract class HostEndpointObservabilityTestsBase : IAsyncLifetime
{
    private const string Sentinel = "/api/sentinel";

    private static readonly HashSet<Type> HostControllers =
    [
        typeof(HostOrdersController),
        typeof(HostPlainController),
        typeof(HostAttributedController),
        typeof(HostActionController),
    ];

    private readonly IDatabaseFixture _fixture;

    protected HostEndpointObservabilityTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task OptedInControllerAction_Success_RowCarriesRouteTemplateAndControllerAction()
    {
        await using var host = await StartAsync(app => app.MapControllers().WithWarpObservability());

        (await host.Client.GetAsync("/api/host-orders/42", Ct)).EnsureSuccessStatusCode();

        await host.PersistAsync(await host.DrainAsync(1));

        var row = await SingleRowAsync();
        row.Method.ShouldBe("GET");
        row.RouteTemplate.ShouldBe("/api/host-orders/{id}");   // the template, never "/api/host-orders/42"
        row.Operation.ShouldBe("HostOrders.Get");
        row.StatusCode.ShouldBe(200);
        row.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task OptedInControllerAction_Throws_FailedRowAndIssueGroup()
    {
        await using var host = await StartAsync(app => app.MapControllers().WithWarpObservability());

        var response = await host.Client.GetAsync("/api/host-orders/boom", Ct);
        ((int)response.StatusCode).ShouldBe(500);

        // The exception escaped before the response started, so the record is deferred to OnCompleted —
        // ReadAsync waits for it.
        await host.PersistAsync(await host.DrainAsync(1));
        await Aggregator().ExecuteAsync(Ct);

        var row = await SingleRowAsync();
        row.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        row.StatusCode.ShouldBe(500);
        row.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        row.Operation.ShouldBe("HostOrders.Boom");

        var group = (await _fixture.CreateContext().Set<ErrorGroup>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();
        group.Source.ShouldBe(ErrorSource.Endpoint);
        group.Kind.ShouldBe(ErrorKind.Exception);
        group.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        group.Culprit.ShouldBe("GET /api/host-orders/boom");
    }

    [TimedFact]
    public async Task ControllerNotOptedIn_NotRecorded()
    {
        await using var host = await StartAsync(app => app.MapControllers());

        (await host.Client.GetAsync("/api/host-plain", Ct)).EnsureSuccessStatusCode();
        (await host.Client.GetAsync("/api/host-orders/42", Ct)).EnsureSuccessStatusCode();

        await host.ShouldRecordOnlySentinelAsync();
    }

    [TimedFact]
    public async Task WarpObservedOnController_RecordedWithoutConvention()
    {
        await using var host = await StartAsync(app => app.MapControllers());

        (await host.Client.GetAsync("/api/host-attributed", Ct)).EnsureSuccessStatusCode();

        await host.PersistAsync(await host.DrainAsync(1));

        var row = await SingleRowAsync();
        row.RouteTemplate.ShouldBe("/api/host-attributed");
        row.Operation.ShouldBe("HostAttributed.Get");
    }

    [TimedFact]
    public async Task WarpObservedOnAction_OnlyThatActionRecorded()
    {
        await using var host = await StartAsync(app => app.MapControllers());

        (await host.Client.GetAsync("/api/host-action/off", Ct)).EnsureSuccessStatusCode();
        (await host.Client.GetAsync("/api/host-action/on", Ct)).EnsureSuccessStatusCode();
        (await host.Client.GetAsync(Sentinel, Ct)).EnsureSuccessStatusCode();

        var records = await host.DrainAsync(2);

        records.Select(x => x.RouteTemplate).ShouldBe(["/api/host-action/on", Sentinel]);
        records[0].Operation.ShouldBe("HostAction.On");
        host.ChannelIsEmpty().ShouldBeTrue();
    }

    [TimedFact]
    public async Task MinimalApi_WithWarpObservability_UsesEndpointName()
    {
        await using var host = await StartAsync(app => app.MapGet("/api/widgets/{id:int}", (int id) => id).WithName("GetWidget").WithWarpObservability());

        (await host.Client.GetAsync("/api/widgets/7", Ct)).EnsureSuccessStatusCode();

        await host.PersistAsync(await host.DrainAsync(1));

        var row = await SingleRowAsync();
        row.Method.ShouldBe("GET");
        row.RouteTemplate.ShouldBe("/api/widgets/{id}");
        row.Operation.ShouldBe("GetWidget");
    }

    [TimedFact]
    public async Task GroupConvention_ObservesEveryEndpointInGroup()
    {
        await using var host = await StartAsync(app =>
        {
            var group = app.MapGroup("/api/grp").WithWarpObservability();
            group.MapGet("/a", () => "a");
            group.MapPost("/b", () => "b");
        });

        (await host.Client.GetAsync("/api/grp/a", Ct)).EnsureSuccessStatusCode();
        (await host.Client.PostAsync("/api/grp/b", null, Ct)).EnsureSuccessStatusCode();

        var records = await host.DrainAsync(2);

        records.Select(x => $"{x.Method} {x.RouteTemplate}").ShouldBe(["GET /api/grp/a", "POST /api/grp/b"]);
    }

    [TimedFact]
    public async Task ExplicitOperationName_WinsOverEndpointName()
    {
        await using var host = await StartAsync(app => app.MapGet("/api/explicit", () => "x").WithName("Ignored").WithWarpObservability("CustomOp"));

        (await host.Client.GetAsync("/api/explicit", Ct)).EnsureSuccessStatusCode();

        (await host.DrainAsync(1)).ShouldHaveSingleItem().Operation.ShouldBe("CustomOp");
    }

    [TimedFact]
    public async Task WarpEndpointInOptedInGroup_KeepsItsOwnIdentity()
    {
        await using var host = await StartAsync(app => app.MapGroup("/wg").WithWarpObservability().MapWarpHttp());

        var endpoint = host.Endpoints()
            .Where(x => string.Equals(x.RoutePattern.RawText, "/wg/inbound", StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        // Exactly one identity — the one MapWarpHttp attached — and the group's Finally step left it alone.
        var identity = endpoint.Metadata.OfType<WarpEndpointIdentity>().ShouldHaveSingleItem();
        identity.Operation.ShouldBe(nameof(InboundHttpHandler));

        (await host.Client.PostAsync("/wg/inbound", null, Ct)).EnsureSuccessStatusCode();

        (await host.DrainAsync(1)).ShouldHaveSingleItem().Operation.ShouldBe(nameof(InboundHttpHandler));
    }

    [TimedFact]
    public async Task DashboardEndpoints_OptedInByHost_NeverObserved()
    {
        await using var host = await StartAsync(app =>
            app.MapWarpDashboard("/warp")
                .WithWarpObservability()
                .WithMetadata(new WarpObservedAttribute()));

        host.Endpoints()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/warp", StringComparison.Ordinal) == true)
            .ShouldAllBe(x => x.Metadata.GetMetadata<WarpEndpointIdentity>() == null);

        (await host.Client.GetAsync("/warp/api/addons", Ct)).EnsureSuccessStatusCode();
        await host.Client.GetAsync("/warp", Ct);

        await host.ShouldRecordOnlySentinelAsync();
    }

    [TimedFact]
    public async Task RecordCallsFailuresOnly_SuccessKeepsCountersButNoRow()
    {
        await using var host = await StartAsync(app => app.MapControllers().WithWarpObservability(), o => o.RecordCalls = CallRecording.FailuresOnly);

        (await host.Client.GetAsync("/api/host-orders/42", Ct)).EnsureSuccessStatusCode();
        (await host.Client.GetAsync("/api/host-orders/boom", Ct)).StatusCode.ShouldBe(System.Net.HttpStatusCode.InternalServerError);

        await host.PersistAsync(await host.DrainAsync(2));

        var row = await SingleRowAsync();
        row.RouteTemplate.ShouldBe("/api/host-orders/boom");

        var successKey = EndpointCounterKeys.Total(EndpointCounterKeys.NormalizeRoute("GET", "/api/host-orders/{id}"), "success");
        var successCounters = await _fixture.CreateContext().Set<Counter>()
            .Where(x => x.Key == successKey)
            .CountAsync(Ct);
        successCounters.ShouldBe(1);
    }

    [TimedFact]
    public async Task OtelSink_ObservedOnMetersOnly_NoRecorderNoRows()
    {
        var calls = new ConcurrentQueue<string?>();
        using var listener = ListenForEndpointCalls("GET /api/host-orders/{id}", calls);

        await using var host = await StartAsync(app => app.MapControllers().WithWarpObservability(), o => o.Sink = RecordingSink.Otel);

        (await host.Client.GetAsync("/api/host-orders/42", Ct)).EnsureSuccessStatusCode();

        // Observed (the always-on meter fired with the bounded route), but the Otel sink registers no recorder
        // and no flusher, so nothing can reach the database.
        calls.ShouldContain("Success");
        host.App.Services.GetServices<IEndpointCallRecorder>().ShouldBeEmpty();
        host.App.Services.GetServices<IHostedService>().ShouldNotContain(x => x is EndpointCallFlusher<TestContext>);
        (await _fixture.CreateContext().Set<EndpointCallLog>().CountAsync(Ct)).ShouldBe(0);
    }

    private static MeterListener ListenForEndpointCalls(string route, ConcurrentQueue<string?> outcomes)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (string.Equals(instrument.Meter.Name, WarpTelemetry.ServiceName, StringComparison.Ordinal)
                    && string.Equals(instrument.Name, "warp.endpoint.calls", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            string? matchedRoute = null;
            string? outcome = null;
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, WarpTelemetryAttributes.EndpointMeterRoute, StringComparison.Ordinal))
                {
                    matchedRoute = tag.Value as string;
                }
                else if (string.Equals(tag.Key, WarpTelemetryAttributes.EndpointMeterOutcome, StringComparison.Ordinal))
                {
                    outcome = tag.Value as string;
                }
            }

            if (string.Equals(matchedRoute, route, StringComparison.Ordinal))
            {
                outcomes.Enqueue(outcome);
            }
        });

        listener.Start();

        return listener;
    }

    private ErrorGroupAggregator<TestContext> Aggregator()
        => new(
            new TestServerContext(_fixture.CreateContext()),
            Options.Create(new WarpServerConfiguration { ErrorGroupingInterval = TimeSpan.FromSeconds(15) }),
            TimeProvider.System,
            TestNotifiers.EmptyDispatcher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ErrorGroupAggregator<TestContext>>.Instance);

    private async Task<EndpointCallLog> SingleRowAsync()
        => (await _fixture.CreateContext().Set<EndpointCallLog>().AsNoTracking().ToListAsync(Ct)).ShouldHaveSingleItem();

    private async Task<Host> StartAsync(Action<WebApplication> map, Action<WarpEndpointObservabilityOptions>? configure = null)
    {
        await using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddDbContext<TestContext>(x =>
        {
            if (isPostgres)
            {
                x.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                x.UseSqlServer(connectionString);
            }
        });

        builder.Services.AddControllers()
            .ConfigureApplicationPartManager(x =>
            {
                x.ApplicationParts.Clear();
                x.ApplicationParts.Add(new AssemblyPart(typeof(HostOrdersController).Assembly));
                foreach (var provider in x.FeatureProviders.OfType<ControllerFeatureProvider>().ToList())
                {
                    x.FeatureProviders.Remove(provider);
                }

                x.FeatureProviders.Add(new OnlyHostControllers());
            });

        builder.Services.AddWarpHttp();
        builder.Services.AddWarp<TestContext>(opt => opt.AddEndpointObservability(o =>
        {
            o.CaptureRequestBodies = CaptureMode.None;
            configure?.Invoke(o);
        }));

        // The test drains the channel itself; the background flusher would race it for the records.
        var flusher = builder.Services.Where(x => x.ImplementationType == typeof(EndpointCallFlusher<TestContext>)).ToList();
        foreach (var descriptor in flusher)
        {
            builder.Services.Remove(descriptor);
        }

        var app = builder.Build();

        // A host exception handler outside Warp's frame (the production shape): it writes the real 500 after
        // the middleware has unwound, so the record is deferred to OnCompleted.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (InvalidOperationException)
            {
                context.Response.StatusCode = 500;
            }
        });

        // The documented order: routing resolves the endpoint, then the observability middleware, then endpoints.
        app.UseRouting();
        app.UseWarpHttpObservability();

        app.MapGet(Sentinel, () => "ok").WithWarpObservability();
        map(app);

        await app.StartAsync(Ct);

        return new Host(app, _fixture);
    }

    private sealed class OnlyHostControllers : ControllerFeatureProvider
    {
        protected override bool IsController(TypeInfo typeInfo) => HostControllers.Contains(typeInfo.AsType());
    }

    private sealed class Host(WebApplication app, IDatabaseFixture fixture) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = app.GetTestClient();

        public IEnumerable<RouteEndpoint> Endpoints()
            => ((IEndpointRouteBuilder)App).DataSources
                .SelectMany(x => x.Endpoints)
                .OfType<RouteEndpoint>();

        public async Task<List<EndpointCallRecord>> DrainAsync(int count)
        {
            var reader = App.Services.GetRequiredService<DbEndpointCallRecorder>().Reader;
            var records = new List<EndpointCallRecord>();
            while (records.Count < count)
            {
                records.Add(await reader.ReadAsync(Ct));
            }

            return records;
        }

        public bool ChannelIsEmpty() => !App.Services.GetRequiredService<DbEndpointCallRecorder>().Reader.TryRead(out _);

        public async Task PersistAsync(IReadOnlyList<EndpointCallRecord> records)
        {
            await using var context = fixture.CreateContext();
            await EndpointCallFlusher<TestContext>.PersistBatchAsync(
                context,
                records,
                App.Services.GetRequiredService<IOptions<WarpConfiguration>>().Value,
                TimeProvider.System,
                Ct);
        }

        // Requests run in order, so a record from an earlier (non-opted) request would be read before the sentinel's.
        public async Task ShouldRecordOnlySentinelAsync()
        {
            (await Client.GetAsync(Sentinel, Ct)).EnsureSuccessStatusCode();

            (await DrainAsync(1)).ShouldHaveSingleItem().RouteTemplate.ShouldBe(Sentinel);
            ChannelIsEmpty().ShouldBeTrue();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(CancellationToken.None);
            await App.DisposeAsync();
        }
    }
}

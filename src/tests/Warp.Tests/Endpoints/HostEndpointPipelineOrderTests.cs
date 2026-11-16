using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Endpoints;
using Warp.Http.Observability;

namespace Warp.Tests.Endpoints;

/// <summary>
/// Pins the documented middleware order for an opted-in host endpoint: the endpoint must be resolved before
/// <c>UseWarpHttpObservability()</c> runs. Explicit <c>UseRouting()</c> first works; omitting <c>UseRouting()</c>
/// on a <c>WebApplication</c> works too (its implicit routing step runs ahead of the host's middleware);
/// registering the middleware BEFORE an explicit <c>UseRouting()</c> sees no endpoint and records nothing.
/// </summary>
[Trait("Category", "NoDb")]
public class HostEndpointPipelineOrderTests
{
    [TimedFact]
    public async Task UseRoutingThenMiddleware_OptedInEndpoint_Recorded()
    {
        var recorder = new CapturingRecorder();
        await using var app = await StartAsync(recorder, x =>
        {
            x.UseRouting();
            x.UseWarpHttpObservability();
        });

        (await app.GetTestClient().GetAsync("/hello/1", Xunit.TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        recorder.Records.ShouldHaveSingleItem().RouteTemplate.ShouldBe("/hello/{id}");
    }

    [TimedFact]
    public async Task NoExplicitUseRouting_OptedInEndpoint_Recorded()
    {
        var recorder = new CapturingRecorder();
        await using var app = await StartAsync(recorder, x => x.UseWarpHttpObservability());

        (await app.GetTestClient().GetAsync("/hello/1", Xunit.TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        recorder.Records.ShouldHaveSingleItem().RouteTemplate.ShouldBe("/hello/{id}");
    }

    [TimedFact]
    public async Task MiddlewareBeforeUseRouting_NothingRecorded()
    {
        var recorder = new CapturingRecorder();
        await using var app = await StartAsync(recorder, x =>
        {
            x.UseWarpHttpObservability();
            x.UseRouting();
        });

        (await app.GetTestClient().GetAsync("/hello/1", Xunit.TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();

        recorder.Records.ShouldBeEmpty();
    }

    private static async Task<WebApplication> StartAsync(CapturingRecorder recorder, Action<WebApplication> pipeline)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IEndpointCallRecorder>(recorder);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<WarpConfiguration>(_ => { });

        var app = builder.Build();
        pipeline(app);
        app.MapGet("/hello/{id:int}", (int id) => id).WithWarpObservability();

        await app.StartAsync(Xunit.TestContext.Current.CancellationToken);

        return app;
    }

    private sealed class CapturingRecorder : IEndpointCallRecorder
    {
        private readonly Lock _gate = new();
        private readonly List<EndpointCallRecord> _records = [];

        public IReadOnlyList<EndpointCallRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public bool Record(EndpointCallRecord record)
        {
            lock (_gate)
            {
                _records.Add(record);
            }

            return true;
        }
    }
}

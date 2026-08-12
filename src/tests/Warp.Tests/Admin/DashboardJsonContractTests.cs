using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.UI.Endpoints;
using Warp.UI.UIMiddleware;

namespace Warp.Tests.Admin;

/// <summary>
/// The dashboard API's wire format belongs to Warp, not to the host process. A host that configures
/// its own JSON options (ConfigureHttpJsonOptions is process-wide for minimal APIs) used to reshape
/// Warp's payloads: with a JsonStringEnumConverter registered, `currentState` serialized as "Failed"
/// instead of 5 and the bundled dashboard — which looks states up numerically — rendered the badge as
/// "Unknown" and dropped the Requeue/Delete actions (they test `kind === 1`).
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardJsonContractTests
{
    private static readonly Guid JobId = Guid.Parse("b47e72bb-d9c4-4f61-ba78-3fc99a58ae66");

    [TimedFact]
    public async Task JobDetail_HostRegistersStringEnumConverter_StillEmitsNumericEnums()
    {
        var (app, client) = await CreateApp(HostAddsStringEnums);
        try
        {
            var response = await client.GetAsync($"/warp/api/detail/{JobId}", CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldContain($"\"currentState\":{(int)State.Failed}");
            body.ShouldContain($"\"kind\":{(int)JobKind.Job}");
            body.ShouldContain($"\"cancellationMode\":{(int)CancellationMode.None}");
            body.ShouldNotContain("\"Failed\"", Case.Sensitive);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task JobList_HostRegistersStringEnumConverter_StillEmitsNumericEnums()
    {
        // Covers the other return shape: this route hands back a bare POCO rather than a
        // Results.Ok(...), so it takes the filter's non-IResult branch. page/pageSize are spelled out
        // because [AsParameters] BaseListRequest exposes them as non-nullable ints, which minimal APIs
        // treat as required — the bundled client always sends both.
        var (app, client) = await CreateApp(HostAddsStringEnums);
        try
        {
            var response = await client.GetAsync("/warp/api/jobs/failed?page=0&pageSize=20", CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
            body.ShouldContain($"\"currentState\":{(int)State.Failed}");
            body.ShouldNotContain("\"Failed\"", Case.Sensitive);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task JobDetail_HostRegistersPascalCaseNamingPolicy_StillEmitsCamelCase()
    {
        // The other half of the same hazard: property names are as much a part of the contract as
        // enum encoding, and the TS client only decodes camelCase.
        var (app, client) = await CreateApp(services =>
            services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null));
        try
        {
            var response = await client.GetAsync($"/warp/api/detail/{JobId}", CancellationToken.None);
            var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            body.ShouldContain("\"currentState\":");
            body.ShouldNotContain("\"CurrentState\":", Case.Sensitive);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task JobDetail_MissingJob_StillReturns404()
    {
        // The filter must leave bodyless results alone — Results.NotFound() carries no value.
        var (app, client) = await CreateApp(HostAddsStringEnums, jobExists: false);
        try
        {
            var response = await client.GetAsync($"/warp/api/detail/{JobId}", CancellationToken.None);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static void HostAddsStringEnums(IServiceCollection services) =>
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    private static async Task<(WebApplication App, HttpClient Client)> CreateApp(
        Action<IServiceCollection> configureServices,
        bool jobExists = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.WebHost.UseDefaultServiceProvider(o => o.ValidateScopes = true);

        var queryService = new Mock<IJobQueryService>();
        queryService
            .Setup(x => x.GetJobDetailById(JobId))
            .ReturnsAsync(jobExists
                ? new UnifiedJobDetailModel
                {
                    Id = JobId,
                    Kind = JobKind.Job,
                    Type = "SyncDirectoryUsersJob",
                    CurrentState = State.Failed,
                    CancellationMode = CancellationMode.None,
                }
                : null);
        queryService
            .Setup(x => x.GetJobsList(It.IsAny<BaseListRequest>(), State.Failed, null))
            .ReturnsAsync(new PagedList<JobModel>(
                1,
                [new JobModel { Id = JobId, Type = "SyncDirectoryUsersJob", CurrentState = State.Failed }],
                1));

        builder.Services.AddScoped(_ => queryService.Object);
        configureServices(builder.Services);

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpUIOptions(), []);

        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }
}

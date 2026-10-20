using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Models;
using Warp.Core.Sagas;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;

namespace Warp.Tests.Admin;

[Trait("Category", "NoDb")]
public class DashboardPagingTests
{
    [TimedTheory]
    [InlineData("page=-3&pageSize=1000000", 0, 500)]
    [InlineData("page=2&pageSize=0", 2, 1)]
    [InlineData("page=1&pageSize=50", 1, 50)]
    public async Task ListEndpoint_QueryStringPaging_ClampedBeforeTheService(string query, int expectedPage, int expectedPageSize)
    {
        BaseListRequest? received = null;
        var queryService = new Mock<IJobQueryService>();
        queryService
            .Setup(x => x.GetJobsList(It.IsAny<BaseListRequest>(), State.Failed, null))
            .Callback<BaseListRequest, State, string?>((request, _, _) => received = request)
            .ReturnsAsync(new PagedList<JobModel>(0, [], 0));
        var (app, client) = await CreateApp(queryService.Object);
        try
        {
            var response = await client.GetAsync($"/warp/api/jobs/failed?{query}", CancellationToken.None);

            response.EnsureSuccessStatusCode();
            received.ShouldNotBeNull();
            received.Page.ShouldBe(expectedPage);
            received.PageSize.ShouldBe(expectedPageSize);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [TimedFact]
    public async Task SagasEndpoint_QueryStringPaging_ClampedBeforeTheService()
    {
        // The sagas route binds page/pageSize itself and builds the request by hand, so the group's
        // filter never sees it; it clamps on its own.
        BaseListRequest? received = null;
        var sagas = new Mock<ISagaQueryService>();
        sagas
            .Setup(x => x.GetSagas(It.IsAny<BaseListRequest>(), null, null))
            .Callback<BaseListRequest, string?, string?>((request, _, _) => received = request)
            .ReturnsAsync(new PagedList<SagaListItemModel>(0, [], 0));
        var (app, client) = await CreateApp(new Mock<IJobQueryService>().Object, sagas.Object);
        try
        {
            var response = await client.GetAsync("/warp/api/sagas?page=-3&pageSize=1000000", CancellationToken.None);

            response.EnsureSuccessStatusCode();
            received.ShouldNotBeNull();
            received.Page.ShouldBe(0);
            received.PageSize.ShouldBe(500);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateApp(IJobQueryService queryService, ISagaQueryService? sagas = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped(_ => queryService);
        if (sagas is not null)
        {
            builder.Services.AddScoped(_ => sagas);
        }

        var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);

        return (app, app.GetTestClient());
    }
}

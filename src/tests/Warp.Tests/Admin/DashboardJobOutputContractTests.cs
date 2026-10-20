using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;

namespace Warp.Tests.Admin;

/// <summary>
/// Pins the JSON the SPA reads from the job output, progress and trace graph routes. The UI tests run
/// against the demo adapter, so a renamed property here would otherwise only show up in a browser.
/// </summary>
[Trait("Category", "NoDb")]
public class DashboardJobOutputContractTests
{
    private static readonly Guid JobId = Guid.Parse("4f0b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d");

    [TimedFact]
    public async Task JobLogs_ReturnsSliceShape()
    {
        var body = await GetJsonAsync($"/warp/api/jobs/{JobId}/logs?page=1&pageSize=2");

        body.GetProperty("page").GetInt32().ShouldBe(1);
        body.GetProperty("pageSize").GetInt32().ShouldBe(2);
        body.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        body.GetProperty("items")[0].GetProperty("message").GetString().ShouldBe("line");
    }

    [TimedFact]
    public async Task JobProgress_ReturnsBarList()
    {
        var body = await GetJsonAsync($"/warp/api/jobs/{JobId}/progress");

        var bar = body.EnumerateArray().ShouldHaveSingleItem();
        bar.GetProperty("name").GetString().ShouldBe("download");
        bar.GetProperty("value").GetInt32().ShouldBe(40);
        bar.TryGetProperty("updatedAt", out _).ShouldBeTrue();
    }

    [TimedFact]
    public async Task TraceGraph_ReturnsJobsAndTruncationFlag()
    {
        var body = await GetJsonAsync($"/warp/api/trace/{JobId}");

        body.GetProperty("isTruncated").GetBoolean().ShouldBeTrue();
        body.GetProperty("jobs").GetArrayLength().ShouldBe(1);
    }

    private static async Task<JsonElement> GetJsonAsync(string path)
    {
        var logs = new Mock<IJobLogQueryService>();
        logs.Setup(x => x.GetHandlerLogs(JobId, It.IsAny<BaseListRequest>()))
            .ReturnsAsync(new PagedSlice<JobLogModel> { Items = [new JobLogModel { Message = "line" }], HasMore = true, Page = 1, PageSize = 2 });
        logs.Setup(x => x.GetProgress(JobId))
            .ReturnsAsync([new JobProgressModel { Name = "download", Value = 40, UpdatedAt = DateTime.UtcNow }]);

        var jobs = new Mock<IJobQueryService>();
        jobs.Setup(x => x.GetTraceGraph(JobId))
            .ReturnsAsync(new TraceGraphModel { Jobs = [new TraceJobModel { Id = JobId }], IsTruncated = true });

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddScoped(_ => logs.Object);
        builder.Services.AddScoped(_ => jobs.Object);

        await using var app = builder.Build();
        app.MapWarpApiEndpoints(new WarpDashboardOptions(), []);
        await app.StartAsync(CancellationToken.None);
        using var client = app.GetTestClient();

        var response = await client.GetAsync(path, CancellationToken.None);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Models;
using Warp.Core.Services;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Dashboard.Extensions;

namespace Warp.Tests.Features.Sagas;

/// <summary>
/// Endpoint tests verify the hide-on-404 behaviour (without the saga services registered)
/// and the happy path (with fake services). Pure NoDb in-memory via TestServer.
/// </summary>
[Trait("Category", "NoDb")]
public class SagaEndpointsTests
{
    [TimedFact]
    public async Task Sagas_WithoutAddon_Returns404()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: false);

        var statsResponse = await ctx.Client.GetAsync(new Uri("/warp/api/sagas/stats", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);
        statsResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var listResponse = await ctx.Client.GetAsync(new Uri("/warp/api/sagas?page=0&pageSize=20", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);
        listResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var typesResponse = await ctx.Client.GetAsync(new Uri("/warp/api/sagas/types", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);
        typesResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task Sagas_WithAddon_ReturnsStats()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true);

        var response = await ctx.Client.GetAsync(new Uri("/warp/api/sagas/stats", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task Sagas_ForceCompleteUnknownId_Returns404()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true);

        var response = await ctx.Client.DeleteAsync(new Uri($"/warp/api/sagas/{Guid.NewGuid()}", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task Sagas_GetByIdUnknown_Returns404()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true);

        var response = await ctx.Client.GetAsync(new Uri($"/warp/api/sagas/{Guid.NewGuid()}", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TimedFact]
    public async Task Sagas_List_ReturnsPagedSagaList()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true, populated: true);

        var response = await ctx.Client.GetAsync(new Uri("/warp/api/sagas?page=0&pageSize=20", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.ShouldContain("\"totalCount\":2");
        body.ShouldContain("\"correlationKey\":\"k-1\"");
    }

    [TimedFact]
    public async Task Sagas_Types_ReturnsDistinctTypes()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true, populated: true);

        var response = await ctx.Client.GetAsync(new Uri("/warp/api/sagas/types", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.ShouldContain("Test.Saga");
    }

    [TimedFact]
    public async Task Sagas_List_PageSizeZero_DefaultsTo20_DoesNotError()
    {
        await using var ctx = await CreateAppAsync(registerSagaServices: true, populated: true);

        // pageSize=0 used to be a footgun (potential divide-by-zero in pagers); the endpoint
        // coerces it to a sane default so clients sending a missing/zero param still succeed.
        var response = await ctx.Client.GetAsync(new Uri("/warp/api/sagas?page=0&pageSize=0", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TimedFact]
    public async Task Sagas_Activity_ReturnsActivityEntries()
    {
        // Activity endpoint always returns 200 — empty list when the saga doesn't exist or has
        // no linked jobs. Route resolution: /sagas/{id}/activity must not collide with /sagas/{id}.
        await using var ctx = await CreateAppAsync(registerSagaServices: true, populated: true);

        var response = await ctx.Client.GetAsync(new Uri($"/warp/api/sagas/{Guid.NewGuid()}/activity", UriKind.Relative), Xunit.TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(Xunit.TestContext.Current.CancellationToken);
        body.ShouldContain("Test.Msg");
    }

    private static async Task<TestAppContext> CreateAppAsync(bool registerSagaServices, bool populated = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        if (registerSagaServices)
        {
            builder.Services.AddSingleton<ISagaQueryService>(new FakeSagaQueryService(populated));
            builder.Services.AddSingleton<ISagaCommandService, FakeSagaCommandService>();
        }

        var app = builder.Build();
        var options = new WarpDashboardOptions();
        var extensions = new List<IWarpDashboardExtension>();
        app.MapWarpApiEndpoints(options, extensions);

        await app.StartAsync(CancellationToken.None);

        return new TestAppContext(app, app.GetTestClient());
    }

    private sealed class TestAppContext(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App { get; } = app;

        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class FakeSagaQueryService : ISagaQueryService
    {
        private readonly bool _populated;

        public FakeSagaQueryService(bool populated) => _populated = populated;

        public Task<PagedList<SagaListItemModel>> GetSagas(BaseListRequest request, string? type, string? correlationKeyContains)
        {
            if (!_populated)
            {
                return Task.FromResult(new PagedList<SagaListItemModel>(0, [], 0));
            }

            var items = new List<SagaListItemModel>
            {
                new() { Id = Guid.NewGuid(), Type = "Test.Saga", CorrelationKey = "k-1", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new() { Id = Guid.NewGuid(), Type = "Test.Saga", CorrelationKey = "k-2", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            };

            return Task.FromResult(new PagedList<SagaListItemModel>(2, items, 0));
        }

        public Task<SagaDetailModel?> GetSagaById(Guid id) => Task.FromResult<SagaDetailModel?>(null);

        public Task<SagaActivityResponseModel> GetSagaActivity(Guid id)
        {
            if (!_populated)
            {
                return Task.FromResult(new SagaActivityResponseModel { Entries = [], TotalInvocations = 0, IsTruncated = false });
            }

            return Task.FromResult(new SagaActivityResponseModel
            {
                Entries = [new() { JobId = Guid.NewGuid(), MessageType = "Test.Msg", JobState = "Completed", CreateTime = DateTime.UtcNow, Logs = [] }],
                TotalInvocations = 1,
                IsTruncated = false,
            });
        }

        public Task<IReadOnlyList<string>> GetSagaTypes()
            => Task.FromResult<IReadOnlyList<string>>(_populated ? ["Test.Saga"] : []);

        public Task<SagaStatsModel> GetStats() => Task.FromResult(new SagaStatsModel { LiveSagas = 0, StartedToday = 0, CompletedToday = 0 });
    }

    private sealed class FakeSagaCommandService : ISagaCommandService
    {
        public Task<bool> ForceComplete(Guid sagaId) => Task.FromResult(false);
    }
}

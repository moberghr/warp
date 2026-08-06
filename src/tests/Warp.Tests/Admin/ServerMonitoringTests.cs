using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

[GenerateDatabaseTests]
public abstract class ServerMonitoringTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ServerMonitoringTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task GetServers_ReturnsServerWithWorkers()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 2,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = Guid.NewGuid(),
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var servers = await svc.GetServers();

        // Assert
        servers.Count.ShouldBe(1);
        servers[0].Id.ShouldBe(serverId);
        servers[0].Workers.Count.ShouldBe(2);
    }

    [TimedFact]
    public async Task GetServerById_ReturnsServerDetail()
    {
        // Arrange
        var ctx = _fixture.CreateContext();
        var serverId = Guid.NewGuid();
        var workerId = Guid.NewGuid();

        ctx.Set<Server>().Add(new Server
        {
            Id = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
            ServiceCount = 1,
        });
        ctx.Set<Warp.Core.Data.Entities.Worker>().Add(new Warp.Core.Data.Entities.Worker
        {
            Id = workerId,
            ServerId = serverId,
            StartedTime = DateTime.UtcNow,
            LastHeartbeatTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var server = await svc.GetServerById(serverId);

        // Assert
        server.ShouldNotBeNull();
        server.Id.ShouldBe(serverId);
        server.ServiceCount.ShouldBe(1);
        server.Workers.Count.ShouldBe(1);
        server.Workers[0].WorkerId.ShouldBe(workerId);
    }

    [TimedFact]
    public async Task GetServerById_NonExistent_ReturnsNull()
    {
        // Act
        var svc = new DashboardStatsService<TestContext>(_fixture.CreateContext(), TimeProvider.System, new Warp.Core.Metrics.LocalMetricSource<TestContext>(_fixture.CreateContext()));
        var server = await svc.GetServerById(Guid.NewGuid());

        // Assert
        server.ShouldBeNull();
    }
}

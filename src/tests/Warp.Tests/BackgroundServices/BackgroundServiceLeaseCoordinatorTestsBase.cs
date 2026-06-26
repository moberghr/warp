using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Entities;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Worker;
using Warp.Worker.BackgroundServices;

namespace Warp.Tests.BackgroundServices;

[GenerateDatabaseTests]
public abstract class BackgroundServiceLeaseCoordinatorTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid MyServerId = Guid.NewGuid();
    private static readonly Guid OtherServerId = Guid.NewGuid();

    protected BackgroundServiceLeaseCoordinatorTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedServerAsync(MyServerId, "test-server-lease-mine");
        await _fixture.SeedServerAsync(OtherServerId, "test-server-lease-other");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BackgroundServiceLeaseCoordinator<TestContext> CreateCoordinator()
    {
        var ctx = _fixture.CreateContext();
        var options = Options.Create(new WarpServerConfiguration { ServerId = MyServerId });

        return new BackgroundServiceLeaseCoordinator<TestContext>(new TestServerContext(ctx), TimeProvider.System, options, TestTasks.QueriesFor(ctx));
    }

    private async Task SeedDefinitionAsync(string serviceName)
    {
        var ctx = _fixture.CreateContext();
        ctx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = serviceName,
            DeclaredScope = ServiceScope.Singleton,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });

        await ctx.SaveChangesAsync(TestCancellation);
    }

    [TimedFact]
    public async Task TryAcquireAsync_NoExistingLease_ReturnsTrueAndInsertsRow()
    {
        await SeedDefinitionAsync("NewLeaseSvc");

        var coordinator = CreateCoordinator();
        var ttl = TimeSpan.FromSeconds(30);

        var acquired = await coordinator.TryAcquireAsync("NewLeaseSvc", ttl, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["NewLeaseSvc"], TestCancellation);

        acquired.ShouldBeTrue();
        lease.ShouldNotBeNull();
        lease.HolderServerId.ShouldBe(MyServerId);
        lease.LeaseExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task TryAcquireAsync_ExpiredLease_ReturnsTrueAndTakesOver()
    {
        await SeedDefinitionAsync("ExpiredLeaseSvc");

        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "ExpiredLeaseSvc",
            HolderServerId = OtherServerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-60),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var coordinator = CreateCoordinator();
        var acquired = await coordinator.TryAcquireAsync("ExpiredLeaseSvc", TimeSpan.FromSeconds(30), TestCancellation);

        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["ExpiredLeaseSvc"], TestCancellation);

        acquired.ShouldBeTrue();
        lease.ShouldNotBeNull();
        lease.HolderServerId.ShouldBe(MyServerId);
        lease.LeaseExpiresAt.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    [TimedFact]
    public async Task TryAcquireAsync_LiveLeaseHeldByOtherServer_ReturnsFalse()
    {
        await SeedDefinitionAsync("LiveLeaseSvc");

        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "LiveLeaseSvc",
            HolderServerId = OtherServerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var coordinator = CreateCoordinator();
        var acquired = await coordinator.TryAcquireAsync("LiveLeaseSvc", TimeSpan.FromSeconds(30), TestCancellation);

        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["LiveLeaseSvc"], TestCancellation);

        acquired.ShouldBeFalse();
        lease.ShouldNotBeNull();
        lease.HolderServerId.ShouldBe(OtherServerId);
    }

    [TimedFact]
    public async Task TryAcquireAsync_LiveLeaseHeldByMe_ReturnsTrueAndExtends()
    {
        await SeedDefinitionAsync("OwnLeaseSvc");

        var originalExpiry = DateTime.UtcNow.AddSeconds(5);
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "OwnLeaseSvc",
            HolderServerId = MyServerId,
            LeaseExpiresAt = originalExpiry,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var coordinator = CreateCoordinator();
        var acquired = await coordinator.TryAcquireAsync("OwnLeaseSvc", TimeSpan.FromSeconds(30), TestCancellation);

        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["OwnLeaseSvc"], TestCancellation);

        acquired.ShouldBeTrue();
        lease.ShouldNotBeNull();
        lease.HolderServerId.ShouldBe(MyServerId);
        lease.LeaseExpiresAt.ShouldBeGreaterThan(originalExpiry);
    }

    [TimedFact]
    public async Task ReleaseAsync_DeletesOnlyOwnLease()
    {
        await SeedDefinitionAsync("ReleaseSvcA");
        await SeedDefinitionAsync("ReleaseSvcB");

        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "ReleaseSvcA",
            HolderServerId = MyServerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30),
        });
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "ReleaseSvcB",
            HolderServerId = OtherServerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var coordinator = CreateCoordinator();
        await coordinator.ReleaseAsync("ReleaseSvcA", TestCancellation);

        var readCtx = _fixture.CreateContext();
        var ownLease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["ReleaseSvcA"], TestCancellation);
        var otherLease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["ReleaseSvcB"], TestCancellation);

        ownLease.ShouldBeNull();
        otherLease.ShouldNotBeNull();
        otherLease.HolderServerId.ShouldBe(OtherServerId);
    }

    [TimedFact]
    public async Task ReleaseAsync_WhenNotHolder_DoesNotDeleteRow()
    {
        await SeedDefinitionAsync("NotMyLeaseSvc");

        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceLease>().Add(new BackgroundServiceLease
        {
            ServiceName = "NotMyLeaseSvc",
            HolderServerId = OtherServerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(30),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var coordinator = CreateCoordinator();
        await coordinator.ReleaseAsync("NotMyLeaseSvc", TestCancellation);

        var readCtx = _fixture.CreateContext();
        var lease = await readCtx.Set<BackgroundServiceLease>()
            .FindAsync(["NotMyLeaseSvc"], TestCancellation);

        lease.ShouldNotBeNull();
        lease.HolderServerId.ShouldBe(OtherServerId);
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;
}

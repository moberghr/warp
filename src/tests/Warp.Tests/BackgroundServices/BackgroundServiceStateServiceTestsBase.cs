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
public abstract class BackgroundServiceStateServiceTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;
    private static readonly Guid MyServerId = Guid.NewGuid();

    protected BackgroundServiceStateServiceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ResetAsync();
        await _fixture.SeedServerAsync(MyServerId, "test-server-state");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private BackgroundServiceStateService<TestContext> CreateService()
    {
        var ctx = _fixture.CreateContext();
        var options = Options.Create(new WarpServerConfiguration { ServerId = MyServerId });

        return new BackgroundServiceStateService<TestContext>(new TestServerContext(ctx), TimeProvider.System, options, TestTasks.QueriesFor(ctx));
    }

    [TimedFact]
    public async Task RegisterAsync_FirstCall_InsertsDefinitionAndInstance()
    {
        var svc = CreateService();

        var outcome = await svc.RegisterAsync("MyService", ServiceScope.PerServer, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var def = await readCtx.Set<BackgroundServiceDefinition>()
            .FindAsync(["MyService"], TestCancellation);
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "MyService"], TestCancellation);

        outcome.ShouldBe(RegistrationOutcome.Registered);
        def.ShouldNotBeNull();
        def.DeclaredScope.ShouldBe(ServiceScope.PerServer);
        def.FirstSeenAt.ShouldBeGreaterThan(default);
        def.LastSeenAt.ShouldBeGreaterThan(default);
        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.Running);
        inst.DeclaredScope.ShouldBe(ServiceScope.PerServer);
    }

    [TimedFact]
    public async Task RegisterAsync_SingletonScope_InstanceStatusIsWaiting()
    {
        var svc = CreateService();

        var outcome = await svc.RegisterAsync("MySingleton", ServiceScope.Singleton, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "MySingleton"], TestCancellation);

        outcome.ShouldBe(RegistrationOutcome.Registered);
        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.Waiting);
    }

    [TimedFact]
    public async Task RegisterAsync_DefinitionExistsSameScope_DoesNotDuplicateDefinition()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "ExistingService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow.AddMinutes(-10),
            LastSeenAt = DateTime.UtcNow.AddMinutes(-10),
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        var outcome = await svc.RegisterAsync("ExistingService", ServiceScope.PerServer, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var defCount = readCtx.Set<BackgroundServiceDefinition>()
            .Where(x => x.Name == "ExistingService")
            .Count();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "ExistingService"], TestCancellation);

        outcome.ShouldBe(RegistrationOutcome.Registered);
        defCount.ShouldBe(1);
        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.Running);
    }

    [TimedFact]
    public async Task RegisterAsync_DefinitionExistsDifferentScope_ReturnsConfigurationMismatch()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "MismatchService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        var outcome = await svc.RegisterAsync("MismatchService", ServiceScope.Singleton, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "MismatchService"], TestCancellation);

        outcome.ShouldBe(RegistrationOutcome.ConfigurationMismatch);
        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.ConfigurationMismatch);
    }

    [TimedFact]
    public async Task SetStatusAsync_UpdatesStatusAndPersistsAcrossContexts()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "StatusService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = MyServerId,
            ServiceName = "StatusService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        await svc.SetStatusAsync("StatusService", BackgroundServiceStatus.Faulted, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "StatusService"], TestCancellation);

        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.Faulted);
    }

    [TimedFact]
    public async Task RecordFaultAsync_SetsLastErrorAndIncrementsRestartCount()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "FaultService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = MyServerId,
            ServiceName = "FaultService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 2,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var ex = new InvalidOperationException("Something went wrong");
        var svc = CreateService();
        await svc.RecordFaultAsync("FaultService", ex, TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "FaultService"], TestCancellation);

        inst.ShouldNotBeNull();
        inst.Status.ShouldBe(BackgroundServiceStatus.Faulted);
        inst.RestartCount.ShouldBe(3);
        inst.LastError.ShouldNotBeNull();
        inst.LastError.ShouldContain("InvalidOperationException");
        inst.LastError.ShouldContain("Something went wrong");
        inst.LastErrorAt.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task ResetRestartCountAsync_SetsRestartCountToZero()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "ResetService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = MyServerId,
            ServiceName = "ResetService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 5,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        await svc.ResetRestartCountAsync("ResetService", TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "ResetService"], TestCancellation);

        inst.ShouldNotBeNull();
        inst.RestartCount.ShouldBe(0);
    }

    [TimedFact]
    public async Task DeleteAsync_RemovesInstanceRow()
    {
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "DeleteService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = MyServerId,
            ServiceName = "DeleteService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        await svc.DeleteAsync("DeleteService", TestCancellation);

        var readCtx = _fixture.CreateContext();
        var inst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "DeleteService"], TestCancellation);

        inst.ShouldBeNull();

        // Definition row should remain (audit record).
        var def = await readCtx.Set<BackgroundServiceDefinition>()
            .FindAsync(["DeleteService"], TestCancellation);
        def.ShouldNotBeNull();
    }

    [TimedFact]
    public async Task DeleteAsync_OnlyRemovesOwnServerInstance()
    {
        var otherServerId = Guid.NewGuid();
        await _fixture.SeedServerAsync(otherServerId, "test-server-state-other");
        var arrangeCtx = _fixture.CreateContext();
        arrangeCtx.Set<BackgroundServiceDefinition>().Add(new BackgroundServiceDefinition
        {
            Name = "SharedService",
            DeclaredScope = ServiceScope.PerServer,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = DateTime.UtcNow,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = MyServerId,
            ServiceName = "SharedService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        arrangeCtx.Set<BackgroundServiceInstance>().Add(new BackgroundServiceInstance
        {
            ServerId = otherServerId,
            ServiceName = "SharedService",
            DeclaredScope = ServiceScope.PerServer,
            Status = BackgroundServiceStatus.Running,
            StartedAt = DateTime.UtcNow,
            LastHeartbeatAt = DateTime.UtcNow,
            RestartCount = 0,
        });
        await arrangeCtx.SaveChangesAsync(TestCancellation);

        var svc = CreateService();
        await svc.DeleteAsync("SharedService", TestCancellation);

        var readCtx = _fixture.CreateContext();
        var myInst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([MyServerId, "SharedService"], TestCancellation);
        var otherInst = await readCtx.Set<BackgroundServiceInstance>()
            .FindAsync([otherServerId, "SharedService"], TestCancellation);

        myInst.ShouldBeNull();
        otherInst.ShouldNotBeNull();
    }

    private static CancellationToken TestCancellation => Xunit.TestContext.Current.CancellationToken;
}

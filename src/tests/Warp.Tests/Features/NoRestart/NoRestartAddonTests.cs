using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.NoRestart;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Features.NoRestart;

[GenerateDatabaseTests]
public abstract class NoRestartAddonTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected NoRestartAddonTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ServiceProvider BuildProvider(bool registerAddon)
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddLogging();
        services.AddScoped<TestContext>(_ => _fixture.CreateContext());
        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOptions<WarpConfiguration>>(new OptionsWrapper<WarpConfiguration>(new WarpConfiguration()));

        if (registerAddon)
        {
            new Warp.Core.WarpBuilder<TestContext>(services).AddNoRestart();
        }

        return services.BuildServiceProvider();
    }

    [TimedFact]
    public async Task NoRestartAttribute_AddonRegistered_SetsCanBeRestartedFalse()
    {
        var provider = BuildProvider(registerAddon: true);
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new NoRestartAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata);
        metadata.ShouldContainKey(nameof(ICanBeRestartedMetadata.CanBeRestarted));
        metadata[nameof(ICanBeRestartedMetadata.CanBeRestarted)].ShouldBe(false);
    }

    [TimedFact]
    public async Task RestartAttribute_AddonRegistered_SetsCanBeRestartedTrue()
    {
        var provider = BuildProvider(registerAddon: true);
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new RestartAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata);
        metadata[nameof(ICanBeRestartedMetadata.CanBeRestarted)].ShouldBe(true);
    }

    [TimedFact]
    public async Task NoRestartAttribute_AddonNotRegistered_AttributeIgnored()
    {
        // Proves the feature is truly opt-in: without AddWarpNoRestart(), the publish pipeline
        // does not inspect the attribute and no metadata is written.
        var provider = BuildProvider(registerAddon: false);
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new NoRestartAttributeRequest());
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.Metadata.ShouldBeNull();
    }

    [TimedFact]
    public async Task WithRestart_AddonNotRegistered_ExplicitMetadataStillPersisted()
    {
        // .WithRestart() writes directly to JobParameters.Metadata, so it bypasses the
        // publish behavior entirely — the value must persist even without the addon.
        var provider = BuildProvider(registerAddon: false);
        await using var scope = provider.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TestContext>();
        var publisher = new Publisher<TestContext>(ctx, TimeProvider.System, scope.ServiceProvider, TestTasks.NullTransport, TestTasks.NullSignals);

        var jobId = await publisher.Enqueue(new UnitRequest(), new Warp.Core.Helper.JobParameters().WithRestart(canBeRestarted: false));
        await publisher.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var job = await _fixture.CreateContext().Set<Job>().FirstAsync(x => x.Id == jobId, Xunit.TestContext.Current.CancellationToken);
        job.Metadata.ShouldNotBeNull();
        var metadata = MetadataSerializer.Deserialize(job.Metadata);
        metadata[nameof(ICanBeRestartedMetadata.CanBeRestarted)].ShouldBe(false);
    }
}

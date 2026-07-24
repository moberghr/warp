using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers.Generated;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Applications;

/// <summary>
/// Regression coverage for routed-message provenance (§ multi-app observability): when a publisher carrying
/// an <see cref="WarpConfiguration.ApplicationName"/> stages an <see cref="Warp.Core.Handlers.IMessage"/>,
/// the <c>MessageRouter</c> must copy that <c>Application</c> onto every routed handler job it creates — a
/// routed job is attributed to the app that created it, not left null. Drives the real publish path
/// (which stamps the Message row) then the real router path (which fans out the children).
/// </summary>
[GenerateDatabaseTests(SerializeInCollection = "HeavyIntegration")]
public abstract class RoutedMessageProvenanceTestsBase : IAsyncLifetime
{
    private const string AppName = "app-x";

    private readonly IDatabaseFixture _fixture;

    protected RoutedMessageProvenanceTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task RunMessageRouting_ParentMessageCarriesApplication_StampsRoutedChildJobs()
    {
        // Arrange: publish an IMessage from a publisher configured with ApplicationName = "app-x". The
        // publish path stamps Application onto the Message row.
        var publisher = new Publisher<TestContext>(
            _fixture.CreateContext(),
            Options.Create(new WarpConfiguration { ApplicationName = AppName }),
            TimeProvider.System,
            new ServiceCollection().BuildServiceProvider(),
            TestTasks.NullTransport,
            TestTasks.NullSignals);

        var messageId = await publisher.Publish(new SingleHandlerMessage());
        await publisher.SaveChangesAsync(Ct);

        // Act: MessageRouter picks up the Enqueued message and fans it out to its handler job(s).
        var scopeFactory = BuildScopeFactory();
        var task = TestTasks.CreateMessageRouter(_fixture.CreateContext(), scopeFactory, TimeProvider.System);
        await task.RunMessageRoutingAsync(Ct);

        // Assert: the routed handler job(s) inherit the parent message's Application.
        var readCtx = _fixture.CreateContext();
        var children = await readCtx.Set<Job>()
            .Where(x => x.ParentJobId == messageId)
            .Where(x => x.Kind == JobKind.Job)
            .ToListAsync(Ct);

        children.ShouldNotBeEmpty();
        children.ShouldAllBe(x => x.Application == AppName);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static IServiceScopeFactory BuildScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddWarpMediator();
        services.AddSingleton<MultiHandlerCounter>();

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Core.Handlers;
using Warp.Tests.Fixtures;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Core;

// Issue #321 end to end, through the real AddWarp registration and a real database. One long-lived
// scope stands in for a Blazor Server circuit (this is the DI shape of a circuit, not a Blazor host:
// the circuit's scope is an ordinary IServiceScope that lives as long as the browser tab). Two sends
// are in flight from it at the same time, pinned inside their handlers. Without ScopePerSend they would
// share the circuit's one DbContext and EF would throw "A second operation was started on this context
// instance"; with it each handler has its own context, and its IPublisher stages onto that same
// context, so one SaveChanges commits the business row and the job together.
[GenerateDatabaseTests]
public abstract class ScopePerSendCircuitTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected ScopePerSendCircuitTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task TwoConcurrentSendsFromOneLongLivedScope_EachCommitsItsRowAndJobOnItsOwnContext()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var barrier = new BarrierSignal();
        await using var provider = BuildProvider(barrier);
        await using var circuit = provider.CreateAsyncScope();
        var mediator = circuit.ServiceProvider.GetRequiredService<IMediator>();
        var circuitContext = circuit.ServiceProvider.GetRequiredService<TestContext>();

        var first = mediator.Send(new CircuitWriteRequest(), ct);
        var second = mediator.Send(new CircuitWriteRequest(), ct);

        // Both handlers are inside their critical section at once, each holding unsaved changes.
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the first handler never started");
        (await barrier.Running.WaitAsync(TimeSpan.FromSeconds(5), ct)).ShouldBeTrue("the second handler never started");
        barrier.CanFinish.Release(2);

        var results = await Task.WhenAll(first, second);

        results[0].Context.ShouldNotBeSameAs(results[1].Context);
        results[0].Context.ShouldNotBeSameAs(circuitContext);
        results[1].Context.ShouldNotBeSameAs(circuitContext);

        await using var assertCtx = _fixture.CreateContext();
        (await assertCtx.TestLogs.CountAsync(ct)).ShouldBe(2);
        var jobIds = results.Select(x => x.JobId).ToList();
        (await assertCtx.Set<Job>().CountAsync(x => jobIds.Contains(x.Id), ct)).ShouldBe(2);

        // The circuit's own context was never used, so nothing piled up in it.
        circuitContext.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private ServiceProvider BuildProvider(BarrierSignal barrier)
    {
        using var probe = _fixture.CreateContext();
        var connectionString = probe.Database.GetConnectionString()!;
        var isPostgres = probe.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(barrier);
        services.AddDbContext<TestContext>(options =>
        {
            if (isPostgres)
            {
                options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
            }
            else
            {
                options.UseSqlServer(connectionString);
            }
        });
        services.AddWarp<TestContext>(opt => opt.Mediator.ScopePerSend = true);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed record CircuitWriteResult(object Context, Guid JobId);

    public sealed class CircuitWriteRequest : IRequest<CircuitWriteResult>;

    // Resolves when it runs, not in the constructor: the source generator registers this handler in the
    // shared GeneratedMediator, which resolves every handler at construction in hosts without these services.
    public sealed class CircuitWriteHandler(IServiceProvider services) : IRequestHandler<CircuitWriteRequest, CircuitWriteResult>
    {
        public async Task<CircuitWriteResult> HandleAsync(CircuitWriteRequest request, CancellationToken cancellationToken)
        {
            var db = services.GetRequiredService<TestContext>();
            var publisher = services.GetRequiredService<IPublisher>();
            var barrier = services.GetRequiredService<BarrierSignal>();

            db.TestLogs.Add(new TestLog { ProcessedTime = DateTime.UtcNow });
            var jobId = await publisher.Enqueue(new UnitRequest());

            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(cancellationToken);

            // One save on the handler's own context: the business row and the staged job together.
            await db.SaveChangesAsync(cancellationToken);

            return new CircuitWriteResult(db, jobId);
        }
    }
}

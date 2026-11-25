using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Entities;
using Warp.Provider.PostgreSql;

namespace Warp.Tests.Worker;

// #347: single-worker finalization saves the claimed row through Warp's server context, and the run id is the
// concurrency token that keeps a run from writing over a re-claim of its job. Only there: on the application's
// context the token would apply to every EF update of a job the host or the dashboard makes. The server-context
// half is covered by WorkerExecutionIntegrationTests.GivenTheJobIsClaimedAgainWhileItsHandlerRuns_* (single-worker
// mode fails without the token).
[Trait("Category", "NoDb")]
public class RunIdConcurrencyTokenTests
{
    [TimedFact]
    public void RunId_OnTheApplicationsContext_IsNotAConcurrencyToken()
    {
        // Arrange — a context type of its own: EF caches one model per context type and provider for the
        // whole process, and this test must neither read nor seed another test's model.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationContext>(x => x.UseNpgsql("Host=dummy"));
        services.AddWarp<ApplicationContext>(x => x.UsePostgreSql());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // Act
        var property = scope.ServiceProvider.GetRequiredService<ApplicationContext>().Model
            .FindEntityType(typeof(Job))!
            .FindProperty(nameof(Job.RunId))!;

        // Assert
        property.IsConcurrencyToken.ShouldBeFalse();
    }

    private sealed class ApplicationContext(DbContextOptions<ApplicationContext> options) : DbContext(options);
}

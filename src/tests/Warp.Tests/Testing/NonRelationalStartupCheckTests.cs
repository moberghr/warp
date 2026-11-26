using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Worker;

namespace Warp.Tests.Testing;

/// <summary>
/// An application's own test suite swaps its DbContext to EF in-memory while its Program.cs still says
/// <c>UsePostgreSql()</c> / <c>UseSqlServer()</c>. The provider startup checks then have nothing to check and
/// must stand down (they read relational metadata or query the server). Each test starts the real hosted
/// service from the real registration; the positive cases stay covered by <c>PostgresTimestampCheckTests</c>
/// and <c>WarpModelExtensionsTests</c>.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class NonRelationalStartupCheckTests
{
    [TimedFact]
    public async Task PostgresTimestampCheck_InMemoryContext_Skips()
    {
        await Should.NotThrowAsync(() => StartAsync<PostgresTimestampCheckService<TestContext>>(x => x.UsePostgreSql()));
    }

    [TimedFact]
    public async Task PostgresVersionCheck_InMemoryContextOnAServer_Skips()
    {
        await Should.NotThrowAsync(() => StartAsync<PostgresVersionCheckService<TestContext>>(x => x.UsePostgreSql()));
    }

    [TimedFact]
    public async Task SqlServerCompatibilityCheck_InMemoryContextOnAServer_Skips()
    {
        await Should.NotThrowAsync(() => StartAsync<SqlServerCompatibilityCheckService<TestContext>>(x => x.UseSqlServer()));
    }

    [TimedFact]
    public async Task WarpModelValidation_InMemoryContextWithTheWarpModel_Passes()
    {
        await Should.NotThrowAsync(() => StartAsync<WarpModelValidationService<TestContext>>(x => x.UsePostgreSql()));
    }

    // Builds only the check under test, after asserting the server registers it: resolving every IHostedService
    // would also construct the worker-side ones, which need the provider's lock pool and cannot be built over an
    // in-memory context at all (that is what AddWarpTesting is for).
    private static async Task StartAsync<THostedService>(Action<WarpServerBuilder<TestContext>> useProvider)
        where THostedService : IHostedService
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"startup-checks-{Guid.NewGuid():N}"));
        services.AddWarpServer<TestContext>(useProvider);
        services.ShouldContain(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(THostedService));
        await using var provider = services.BuildServiceProvider();

        await ActivatorUtilities
            .CreateInstance<THostedService>(provider)
            .StartAsync(Xunit.TestContext.Current.CancellationToken);
    }
}

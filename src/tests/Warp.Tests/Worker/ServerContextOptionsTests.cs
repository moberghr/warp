using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class ServerContextOptionsTests
{
    [TimedFact]
    public void ServerContextOptions_TwoScopes_AreTheSameInstance()
    {
        // The server context is created for every claim, completion flush, monitor tick and server-task
        // pass; its options are built once for the process, not again for each context (several KB each).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"server-options-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryConfigurator());
        services.AddWarpServer<TestContext>();
        using var provider = services.BuildServiceProvider();

        // Act
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        var firstOptions = first.ServiceProvider.GetRequiredService<IWarpServerContext>().Context.GetService<IDbContextOptions>();
        var secondOptions = second.ServiceProvider.GetRequiredService<IWarpServerContext>().Context.GetService<IDbContextOptions>();

        // Assert
        secondOptions.ShouldBeSameAs(firstOptions);
    }

    private sealed class InMemoryConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"server-options-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
    }
}

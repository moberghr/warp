using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Worker;

namespace Warp.Tests.Worker;

/// <summary>
/// Rejects a counter-buffer flush interval that leaves the flusher spinning instead of waiting.
/// <para>
/// <c>CounterBufferFlushInterval</c> is the ONLY delay in <c>CounterBufferFlusher</c>'s loop.
/// <c>TimeSpan.Zero</c> therefore flushes with no pause at all, and a negative value makes
/// <c>Task.Delay</c> throw on every iteration into a catch that logs the failure and immediately
/// retries — a hot CPU loop writing an unbounded error log, in a process whose jobs still run and
/// whose health checks still pass. Neither shape is reachable from code by accident, but both are
/// one <c>ConfigurationBinder</c> typo away, and neither announces itself at runtime.
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class CounterBufferFlushIntervalValidationTests
{
    private static void RegisterMinimalDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"flush-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryServerContextConfigurator());
    }

    private sealed class InMemoryServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"flush-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
    }

    [TimedTheory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddWarpServer_WithNonPositiveFlushInterval_Throws(int seconds)
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        var ex = Should.Throw<InvalidOperationException>(() =>
            services.AddWarpServer<TestContext>(opt =>
                opt.CounterBufferFlushInterval = TimeSpan.FromSeconds(seconds)));

        // Naming the knob is the point: the symptom is a pegged core and a growing log, which points
        // nowhere near a metrics-flush setting.
        ex.Message.ShouldContain("CounterBufferFlushInterval");
    }

    [TimedFact]
    public void AddWarpServer_WithTheDefaultFlushInterval_DoesNotThrow()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        Should.NotThrow(() => services.AddWarpServer<TestContext>(_ => { }));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Worker;

namespace Warp.Tests.Worker;

/// <summary>
/// Pins the relationship between the heartbeat cadence and the singleton background-service lease TTL.
/// <para>
/// The lease is renewed by <c>Heartbeat</c>, so <c>HealthCheckInterval</c> is the renewal cadence and
/// <c>BackgroundServiceLeaseTtl</c> is the deadline. They are configured independently, which means a
/// deployment can silently end up with a TTL that allows barely one renewal attempt before expiry — one
/// slow round-trip then hands a "singleton" service to a second server while the first is still running
/// it. Nothing about that shape is visible at runtime until it bites, so it is rejected at startup
/// instead, in the same fail-fast block as the metrics-tier ordering (§8.30).
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class LeaseRenewalMarginTests
{
    private static void RegisterMinimalDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(o => o.UseInMemoryDatabase($"lease-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddSingleton<IWarpServerContextConfigurator>(new InMemoryServerContextConfigurator());
    }

    private sealed class InMemoryServerContextConfigurator : IWarpServerContextConfigurator
    {
        private readonly string _database = $"lease-server-{Guid.NewGuid():N}";

        public void Configure(DbContextOptionsBuilder optionsBuilder, IServiceProvider applicationServices)
        {
            optionsBuilder.UseInMemoryDatabase(_database);
        }
    }

    // RED until AddWarpServer validates the margin. A 10s TTL was comfortable against the old 3s
    // heartbeat (three renewal attempts) and is not against a 5s one — exactly the deployment the
    // cadence change silently degrades, and precisely the case a default change must not leave
    // undetected.
    [TimedFact]
    public void AddWarpServer_WithLeaseTtlTooShortForHeartbeat_Throws()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        var ex = Should.Throw<InvalidOperationException>(() =>
            services.AddWarpServer<TestContext>(opt =>
            {
                opt.HealthCheckInterval = TimeSpan.FromSeconds(5);
                opt.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(10);
            }));

        // The message has to name both knobs — an operator hitting this at startup needs to know which
        // of the two to move, and the relationship is not guessable from either one alone.
        ex.Message.ShouldContain("BackgroundServiceLeaseTtl");
        ex.Message.ShouldContain("HealthCheckInterval");
    }

    [TimedFact]
    public void AddWarpServer_WithLeaseTtlGivingThreeRenewals_DoesNotThrow()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        Should.NotThrow(() =>
            services.AddWarpServer<TestContext>(opt =>
            {
                opt.HealthCheckInterval = TimeSpan.FromSeconds(5);
                opt.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(15);
            }));
    }

    // The shipped pair must satisfy its own rule — a default that fails the validation it introduces
    // would break every existing deployment on upgrade.
    [TimedFact]
    public void AddWarpServer_WithDefaultCadences_DoesNotThrow()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        Should.NotThrow(() => services.AddWarpServer<TestContext>(_ => { }));
    }

    // Disabling the heartbeat disables lease renewal entirely; that is a deliberate test/limited shape,
    // not a margin violation, so it must not be caught by the guard.
    [TimedFact]
    public void AddWarpServer_WithHeartbeatDisabled_DoesNotThrow()
    {
        var services = new ServiceCollection();
        RegisterMinimalDependencies(services);

        Should.NotThrow(() =>
            services.AddWarpServer<TestContext>(opt =>
            {
                opt.HealthCheckInterval = null;
                opt.BackgroundServiceLeaseTtl = TimeSpan.FromSeconds(10);
            }));
    }
}

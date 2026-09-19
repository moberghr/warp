using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Warp.Provider.PostgreSql;
using Warp.Tests.TestData;

namespace Warp.Tests.Features.Concurrency;

/// <summary>
/// Rot guard for the advisory-lock pool split. The lock providers must NOT be handed
/// <typeparamref name="TContext"/>'s connection string verbatim — they get one carrying a distinct
/// <c>Application Name</c>, which is part of Npgsql's pool key and is therefore the whole of the
/// separation.
/// <para>
/// Why a test exists for one connection-string property: Npgsql resets a pooled connection with a
/// single <c>DISCARD ALL</c>, but a connector carrying prepared statements must instead be reset with
/// a seven-statement sequence, because <c>DISCARD ALL</c> would deallocate them. Medallion prepares
/// its advisory-lock statements, so sharing one pool with EF flips EVERY connector in the process and
/// ordinary EF round trips start paying that reset too. Measured on the concurrency addon's
/// no-contention load arm: 50.40 statements per job before the split, 24.84 after — and 16.87 to 13.76
/// on a workload carrying no concurrency keys at all, since the server tasks take advisory locks too.
/// </para>
/// <para>
/// None of that is observable from behaviour: locks work identically either way, every existing test
/// passes either way, and a refactor that "simplifies" the resolver back to
/// <c>ResolveConnectionString</c> would silently restore the seven-statement reset. This test is the
/// only thing standing between that edit and a 50% statement regression.
/// </para>
/// </summary>
[Trait("Category", "NoDb")]
public class LockConnectionPoolIsolationTests
{
    private const string BaseConnectionString = "Host=localhost;Port=5432;Database=warp_guard;Username=warp;Password=secret";

    [TimedFact]
    public void LockConnectionString_DiffersFromContextOnlyByApplicationName()
    {
        using var provider = BuildProvider(BaseConnectionString);

        var lockConnectionString = PostgreSqlServiceConfiguration.ResolveLockConnectionString<TestContext>(provider);

        var context = new NpgsqlConnectionStringBuilder(BaseConnectionString);
        var locks = new NpgsqlConnectionStringBuilder(lockConnectionString);

        // Different pool: Application Name participates in Npgsql's pool key.
        locks.ApplicationName.ShouldNotBe(context.ApplicationName);
        locks.ApplicationName.ShouldNotBeNullOrEmpty();

        // Same database, same credentials — the split is a pool boundary, not a second target. An
        // advisory lock taken on either connection must land in the same lock namespace.
        locks.Host.ShouldBe(context.Host);
        locks.Port.ShouldBe(context.Port);
        locks.Database.ShouldBe(context.Database);
        locks.Username.ShouldBe(context.Username);
        locks.Password.ShouldBe(context.Password);
    }

    [TimedFact]
    public void LockConnectionString_PreservesTheHostsOwnApplicationName()
    {
        // Hosts key pg_stat_activity dashboards and connection-level monitoring off Application Name,
        // so it is suffixed rather than overwritten — replacing it would make Warp's lock sessions
        // anonymous in exactly the tooling an operator uses to find them.
        using var provider = BuildProvider($"{BaseConnectionString};Application Name=checkout-api");

        var lockConnectionString = PostgreSqlServiceConfiguration.ResolveLockConnectionString<TestContext>(provider);

        var applicationName = new NpgsqlConnectionStringBuilder(lockConnectionString).ApplicationName;

        applicationName.ShouldStartWith("checkout-api");
        applicationName.ShouldNotBe("checkout-api");
    }

    [TimedFact]
    public void LockConnectionString_PinsMinPoolSizeToZero()
    {
        // MinPoolSize is a per-pool floor. Inheriting a host that pre-warms its DbContext pool would
        // hold that many idle connections a SECOND time, for locks that mostly need one — and would
        // make the doc's "active use is close to unchanged" false.
        using var provider = BuildProvider($"{BaseConnectionString};Minimum Pool Size=25");

        var locks = new NpgsqlConnectionStringBuilder(
            PostgreSqlServiceConfiguration.ResolveLockConnectionString<TestContext>(provider));

        locks.MinPoolSize.ShouldBe(0);
        new NpgsqlConnectionStringBuilder($"{BaseConnectionString};Minimum Pool Size=25").MinPoolSize.ShouldBe(25);
    }

    [TimedFact]
    public void LockConnectionString_KeepsTheSuffixWhenTheHostNameIsTooLongForPostgres()
    {
        // Postgres truncates application_name at NAMEDATALEN-1 (63 bytes). Appending to a name already
        // near that length would drop the suffix server-side, and the lock sessions would show up in
        // pg_stat_activity as indistinguishable from the DbContext's — the opposite of the point.
        var longName = new string('a', 80);
        using var provider = BuildProvider($"{BaseConnectionString};Application Name={longName}");

        var applicationName = new NpgsqlConnectionStringBuilder(
            PostgreSqlServiceConfiguration.ResolveLockConnectionString<TestContext>(provider)).ApplicationName;

        applicationName.ShouldNotBeNull();
        applicationName.Length.ShouldBeLessThanOrEqualTo(63);
        applicationName.ShouldEndWith(":warp-locks");
        applicationName.ShouldStartWith("aaaa");
    }

    private static ServiceProvider BuildProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(options => options.UseNpgsql(connectionString));

        return services.BuildServiceProvider();
    }
}

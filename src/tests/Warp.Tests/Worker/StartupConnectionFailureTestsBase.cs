using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Provider.PostgreSql;
using Warp.Provider.SqlServer;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Worker;

/// <summary>
/// How <see cref="WarpStartupGate"/> classifies what the real providers throw, on both databases: a database that
/// cannot be reached is retried under <see cref="WarpConfiguration.RequireDatabaseAtStartup"/> = false; one that
/// answers with an error (a wrong password, a missing table) is not, so startup still fails as before.
/// </summary>
[GenerateDatabaseTests]
public abstract class StartupConnectionFailureTestsBase : IAsyncLifetime
{
    private readonly IDatabaseFixture _fixture;

    protected StartupConnectionFailureTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task IsDatabaseUnavailable_NothingListensOnThePort_IsTrue()
    {
        var error = await OpenAsync(x => x.WithPort(1));

        Gate().IsDatabaseUnavailable(error).ShouldBeTrue(error.ToString());
    }

    [TimedFact]
    public async Task IsDatabaseUnavailable_WrongPassword_IsFalse()
    {
        var error = await OpenAsync(x => x.WithPassword("not-the-password"));

        Gate().IsDatabaseUnavailable(error).ShouldBeFalse(error.ToString());
    }

    [TimedFact]
    public async Task IsDatabaseUnavailable_StatementTheDatabaseRejects_IsFalse()
    {
        await using var context = _fixture.CreateContext();
        var error = await Should.ThrowAsync<Exception>(() =>
            context.Database.ExecuteSqlRawAsync("SELECT missing_column FROM missing_table_for_startup_test", Xunit.TestContext.Current.CancellationToken));

        Gate().IsDatabaseUnavailable(error).ShouldBeFalse(error.ToString());
    }

    private bool IsPostgreSql()
    {
        using var context = _fixture.CreateContext();

        return context.Database.IsNpgsql();
    }

    private WarpStartupGate Gate()
    {
        IDatabaseExceptionClassifier classifier = IsPostgreSql() ? new PostgresExceptionClassifier() : new SqlServerExceptionClassifier();

        return new WarpStartupGate(
            Microsoft.Extensions.Options.Options.Create(new WarpConfiguration { RequireDatabaseAtStartup = false }),
            TimeProvider.System,
            [],
            classifier);
    }

    // Opens a bare connection to the fixture's server with one setting changed, and returns what it threw.
    private async Task<Exception> OpenAsync(Func<ConnectionChange, ConnectionChange> change)
    {
        await using var context = _fixture.CreateContext();
        var original = context.Database.GetConnectionString()!;
        var wanted = change(new ConnectionChange());
        await using DbConnection connection = IsPostgreSql()
            ? new NpgsqlConnection(wanted.ApplyTo(new NpgsqlConnectionStringBuilder(original) { Pooling = false, Timeout = 3 }))
            : new SqlConnection(wanted.ApplyTo(new SqlConnectionStringBuilder(original) { Pooling = false, ConnectTimeout = 3, ConnectRetryCount = 0 }));

        return await Should.ThrowAsync<Exception>(() => connection.OpenAsync(Xunit.TestContext.Current.CancellationToken));
    }

    private sealed record ConnectionChange(int? Port = null, string? Password = null)
    {
        public ConnectionChange WithPort(int port) => this with { Port = port };

        public ConnectionChange WithPassword(string password) => this with { Password = password };

        public string ApplyTo(NpgsqlConnectionStringBuilder builder)
        {
            builder.Port = Port ?? builder.Port;
            builder.Password = Password ?? builder.Password;

            return builder.ConnectionString;
        }

        public string ApplyTo(SqlConnectionStringBuilder builder)
        {
            if (Port is { } port)
            {
                var host = builder.DataSource.Split(',')[0];
                builder.DataSource = $"{host},{port}";
            }

            builder.Password = Password ?? builder.Password;

            return builder.ConnectionString;
        }
    }
}

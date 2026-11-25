using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.Comparison;

/// <summary>
/// One library under test. The harness drives every arm through the same steps: create the schema, preload the
/// jobs with no server running, start the servers, wait until every job has completed, stop them.
/// </summary>
internal interface IArm : IAsyncDisposable
{
    Task PrepareAsync(CancellationToken ct);

    Task PreloadAsync(int jobs, bool work, int items, CancellationToken ct);

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    Task<long> CountCompletedAsync(CancellationToken ct);

    Task<List<DateTime>> CompletionTimesAsync(CancellationToken ct);
}

/// <summary>
/// Warp, configured the way an application would be: <c>AddWarpServer</c> with the PostgreSQL provider, the worker
/// count, and every other setting at its default. <paramref name="dispatcher"/> turns on dispatcher mode (one claim
/// for the whole worker group, completions written in batches); without it each worker claims its own job.
/// </summary>
internal sealed class WarpArm(string connectionString, string harnessConnectionString, int servers, int workers, bool dispatcher) : IArm
{
    private readonly List<IHost> _hosts = [];
    private ServiceProvider? _harness;

    public async Task PrepareAsync(CancellationToken ct)
    {
        for (var i = 0; i < servers; i++)
        {
            _hosts.Add(Host.CreateDefaultBuilder()
                .ConfigureLogging(x => x.SetMinimumLevel(LogLevel.Warning))
                .ConfigureServices(services =>
                {
                    services.AddDbContext<ComparisonContext>(x => x.UseNpgsql(connectionString));
                    services.AddWarpServer<ComparisonContext>(x =>
                    {
                        x.UsePostgreSql();
                        x.WorkerCount = workers;
                        x.UseDispatcher = dispatcher;
                    });
                })
                .Build());
        }

        // The harness reads through a context of its own, on a connection the measurement leaves out.
        var harness = new ServiceCollection();
        harness.AddDbContext<ComparisonContext>(x => x.UseNpgsql(harnessConnectionString));
        harness.AddWarp<ComparisonContext>(x => x.UsePostgreSql());
        _harness = harness.BuildServiceProvider();

        await using var scope = _hosts[0].Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ComparisonContext>().Database.EnsureCreatedAsync(ct);
    }

    public async Task PreloadAsync(int jobs, bool work, int items, CancellationToken ct)
    {
        // One save per thousand jobs: a host publishing a backlog would batch it the same way.
        for (var start = 0; start < jobs; start += 1000)
        {
            await using var scope = _hosts[0].Services.CreateAsyncScope();
            var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
            for (var i = start; i < Math.Min(jobs, start + 1000); i++)
            {
                if (work)
                {
                    await publisher.Enqueue(new WorkJob { ItemId = (i % items) + 1 });
                }
                else
                {
                    await publisher.Enqueue(new NoopJob());
                }
            }

            await publisher.SaveChangesAsync(ct);
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        foreach (var host in _hosts)
        {
            await host.StartAsync(ct);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync(ct);
        }
    }

    public async Task<long> CountCompletedAsync(CancellationToken ct)
    {
        await using var scope = _harness!.CreateAsyncScope();
        Harness.CountQuery();

        return await scope.ServiceProvider.GetRequiredService<ComparisonContext>().Set<Job>()
            .TagWith(Harness.Tag)
            .Where(x => x.Kind == JobKind.Job)
            .Where(x => x.CurrentState == State.Completed)
            .LongCountAsync(ct);
    }

    public async Task<List<DateTime>> CompletionTimesAsync(CancellationToken ct)
    {
        await using var scope = _harness!.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<ComparisonContext>().Set<JobLog>()
            .TagWith(Harness.Tag)
            .Where(x => x.EventType == "Completed")
            .Select(x => x.Timestamp)
            .ToListAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            host.Dispose();
        }

        if (_harness is not null)
        {
            await _harness.DisposeAsync();
        }
    }
}

/// <summary>
/// Hangfire 1.8 with Hangfire.PostgreSql, with its default filters (automatic retry, statistics, continuations) and
/// one setting changed: <c>QueuePollInterval</c> is 1 s instead of 15 s, so a worker that finds the queue empty
/// does not wait 15 s to look again. Under a preloaded backlog it rarely matters; it is set so it cannot.
/// </summary>
internal sealed class HangfireArm(string connectionString, string harnessConnectionString, int servers, int workers) : IArm
{
    private readonly List<BackgroundJobServer> _servers = [];

    public Task PrepareAsync(CancellationToken ct)
    {
        GlobalConfiguration.Configuration
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                x => x.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions
                {
                    SchemaName = "hangfire",
                    PrepareSchemaIfNecessary = true,
                    QueuePollInterval = TimeSpan.FromSeconds(1),
                });

        // Creates the schema now, not inside the measured window.
        using var connection = JobStorage.Current.GetConnection();

        return Task.CompletedTask;
    }

    public async Task PreloadAsync(int jobs, bool work, int items, CancellationToken ct)
    {
        // Hangfire writes each enqueue on its own; eight publishers so the preload does not take minutes.
        var next = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 8),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            (_, _) =>
            {
                var client = new BackgroundJobClient();
                int i;
                while ((i = Interlocked.Increment(ref next)) <= jobs)
                {
                    var itemId = (i % items) + 1;
                    _ = work ? client.Enqueue(() => HangfireJobs.Work(itemId)) : client.Enqueue(() => HangfireJobs.Noop());
                }

                return ValueTask.CompletedTask;
            });
    }

    public Task StartAsync(CancellationToken ct)
    {
        for (var i = 0; i < servers; i++)
        {
            _servers.Add(new BackgroundJobServer(new BackgroundJobServerOptions { WorkerCount = workers, ServerName = $"comparison-{i}" }));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        foreach (var server in _servers)
        {
            server.SendStop();
        }

        foreach (var server in _servers)
        {
            server.Dispose();
        }

        _servers.Clear();

        return Task.CompletedTask;
    }

    public async Task<long> CountCompletedAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(harnessConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM hangfire.job WHERE statename = 'Succeeded' /* {Harness.Tag} */", connection);
        Harness.CountQuery();

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<List<DateTime>> CompletionTimesAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(harnessConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand($"SELECT createdat FROM hangfire.state WHERE name = 'Succeeded' /* {Harness.Tag} */", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var times = new List<DateTime>();
        while (await reader.ReadAsync(ct))
        {
            times.Add(reader.GetDateTime(0));
        }

        return times;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}

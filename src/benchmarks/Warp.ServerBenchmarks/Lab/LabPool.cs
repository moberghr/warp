using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// How a lab server reaches PostgreSQL. By default the context takes a connection string, so Warp's
/// server context opens on its own pool (rule 6.22). With <c>WARP_LAB_SHARED_POOL=1</c> it takes an
/// <see cref="NpgsqlDataSource"/>, which Warp cannot fork, so the server context shares the
/// application's pool: the two settings measure the separate pool against the shared one. Child node
/// processes inherit the variable.
/// </summary>
public static class LabPool
{
    public static bool Shared { get; } =
        string.Equals(Environment.GetEnvironmentVariable("WARP_LAB_SHARED_POOL"), "1", StringComparison.Ordinal);

    public static DbContextOptionsBuilder UseLabNpgsql(this DbContextOptionsBuilder options, string connectionString) =>
        Shared ? options.UseNpgsql(DataSources.Get(connectionString)) : options.UseNpgsql(connectionString);

    private static class DataSources
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<string, NpgsqlDataSource> Created = new(StringComparer.Ordinal);

        public static NpgsqlDataSource Get(string connectionString)
        {
            lock (Gate)
            {
                if (!Created.TryGetValue(connectionString, out var source))
                {
                    source = NpgsqlDataSource.Create(connectionString);
                    Created[connectionString] = source;
                }

                return source;
            }
        }
    }
}

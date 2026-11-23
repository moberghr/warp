using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Warp.Core;
using Warp.Provider.PostgreSql;
using Warp.Worker;

namespace Warp.ServerBenchmarks.Lab;

/// <summary>
/// What a server process spends building EF models at startup, measured cold in a fresh process: the
/// application's context model and Warp's server-context model, in the order given. No database is
/// opened; building a model needs none. Run each order in its own process: the first build also pays
/// for JIT-compiling EF's model building, so the order matters.
/// </summary>
public static class StartupModelProbe
{
    public static void Run(bool serverFirst)
    {
        var total = Stopwatch.StartNew();
        var services = new ServiceCollection();
        services.AddLogging(x => x.SetMinimumLevel(LogLevel.Error));
        services.AddDbContext<TestContext>(x => x
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention());
        services.AddWarpServer<TestContext>(x =>
        {
            x.UsePostgreSql();
            x.UseDispatcher = true;
        });

        using var provider = services.BuildServiceProvider();
        var built = total.Elapsed;

        var steps = serverFirst
            ? new[] { ("server context model", (Func<IServiceProvider, object>)ServerModel), ("application model", AppModel) }
            : [("application model", AppModel), ("server context model", ServerModel)];

        Console.WriteLine($"service provider built: {built.TotalMilliseconds:N0} ms");
        foreach (var (label, step) in steps)
        {
            using var scope = provider.CreateScope();
            var watch = Stopwatch.StartNew();
            step(scope.ServiceProvider);
            Console.WriteLine($"{label}: {watch.Elapsed.TotalMilliseconds:N0} ms");
        }

        Console.WriteLine($"total: {total.Elapsed.TotalMilliseconds:N0} ms");
    }

    private static object AppModel(IServiceProvider services) => services.GetRequiredService<TestContext>().Model;

    private static object ServerModel(IServiceProvider services) => services.GetRequiredService<IWarpServerContext>().Context.Model;
}

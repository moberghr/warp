using System.Collections.Concurrent;
using System.Diagnostics;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Counts first-chance exceptions by type and throwing frame, so BenchmarkDotNet's bare
/// <c>// Exceptions: N</c> line can be traced to a source. Opt-in via <c>WARP_BENCH_TRACE_EXCEPTIONS</c>:
/// capturing a stack per throw is far too expensive to leave on while timing anything.
/// </summary>
internal static class ExceptionTally
{
    private static readonly ConcurrentDictionary<string, int> Counts = new();
    private static int _started;

    public static void StartIfRequested()
    {
        if (Environment.GetEnvironmentVariable("WARP_BENCH_TRACE_EXCEPTIONS") is null
            || Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var frames = new StackTrace(1, false).GetFrames()
                .Select(x => x.GetMethod())
                .Where(x => x?.DeclaringType != null)
                .Select(x => $"{x!.DeclaringType!.FullName}.{x.Name}")
                .Where(x => !x.StartsWith("System.", StringComparison.Ordinal) && !x.StartsWith("Warp.ServerBenchmarks.Infrastructure.ExceptionTally", StringComparison.Ordinal))
                .Take(4);

            var key = $"{e.Exception.GetType().FullName}: {e.Exception.Message.Split('\n')[0]} @ {string.Join(" <- ", frames)}";
            Counts.AddOrUpdate(key, 1, (_, n) => n + 1);
        };
    }

    public static void Report()
    {
        foreach (var entry in Counts.OrderByDescending(x => x.Value).Take(15))
        {
            Console.WriteLine($"// first-chance {entry.Value,7}  {entry.Key}");
        }
    }
}

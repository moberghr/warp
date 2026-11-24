using System.Diagnostics;

namespace Warp.MatrixBench;

/// <summary>Completion rate from the handler finish times, bucketed per second after the marker.</summary>
public sealed record Throughput(double FirstToLastSeconds, double PerSecond, double P10, double P50, double P90, int[] Histogram)
{
    public static Throughput From(long[] finished, long marker)
    {
        if (finished.Length == 0)
        {
            return new Throughput(0, 0, 0, 0, 0, []);
        }

        var seconds = finished
            .Select(x => Math.Max(0, (x - marker) / (double)Stopwatch.Frequency))
            .Order()
            .ToArray();

        var histogram = new int[(int)seconds[^1] + 1];
        foreach (var second in seconds)
        {
            histogram[(int)second]++;
        }

        var span = seconds[^1] - seconds[0];

        // Whole seconds only: the bins holding the first and the last finish are partial, and a partial bin
        // reads as a slump that never happened. Too short a run to drop them keeps them.
        var first = (int)seconds[0];
        var last = (int)seconds[^1];
        var bins = last - first >= 2
            ? histogram[(first + 1)..last]
            : histogram[first..(last + 1)];
        var sorted = bins.Order().ToArray();

        return new Throughput(
            span,
            span > 0 ? finished.Length / span : finished.Length,
            Percentile(sorted, 0.10),
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.90),
            histogram);
    }

    /// <summary>Nearest rank.</summary>
    private static double Percentile(int[] sorted, double fraction)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(fraction * sorted.Length);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

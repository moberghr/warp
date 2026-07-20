using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Warp.Core.Data.Converters;

// Persists WebhookDelivery.RetrySchedule (IReadOnlyList<TimeSpan>) as a JSON seconds-array text column,
// e.g. [60,600,3600,21600] for [1m,10m,1h,6h]; "[]" for an empty schedule (single attempt). Seconds
// (not a format-sensitive TimeSpan string — spec trap register) so the column is human-readable and
// stable. The paired ValueComparer is mandatory for a mutable reference-typed property: without it EF
// compares the list by reference and misses in-place mutations / snapshots incorrectly.
internal static class RetryScheduleConverter
{
    internal static readonly ValueConverter<IReadOnlyList<TimeSpan>, string> Converter = new(
        v => Serialize(v),
        v => Deserialize(v));

    internal static readonly ValueComparer<IReadOnlyList<TimeSpan>> Comparer = new(
        (a, b) => AreEqual(a, b),
        v => HashOf(v),
        v => Snapshot(v));

    internal static string Serialize(IReadOnlyList<TimeSpan> schedule)
    {
        // System.Text.Json writes an integral double (21600.0) as "21600", giving a clean integer array.
        return JsonSerializer.Serialize(schedule.Select(x => x.TotalSeconds));
    }

    internal static IReadOnlyList<TimeSpan> Deserialize(string value)
    {
        // The dispatcher always writes at least "[]" (empty schedule = single attempt). A whitespace/empty
        // or JSON-null column is therefore corrupt, not "no retries" — silently materializing it as an empty
        // schedule would disable retries on a delivery that asked for them. Fail loud instead.
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                "WebhookDelivery.RetrySchedule column is empty/whitespace. The dispatcher always writes at "
                + "least \"[]\"; a blank value indicates a corrupt row.");
        }

        var seconds = JsonSerializer.Deserialize<double[]>(value)
            ?? throw new InvalidOperationException(
                "WebhookDelivery.RetrySchedule column deserialized to null (JSON literal 'null'). The "
                + "dispatcher always writes at least \"[]\"; a null value indicates a corrupt row.");

        return [.. seconds.Select(TimeSpan.FromSeconds)];
    }

    private static bool AreEqual(IReadOnlyList<TimeSpan>? a, IReadOnlyList<TimeSpan>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        return a.SequenceEqual(b);
    }

    private static int HashOf(IReadOnlyList<TimeSpan> schedule)
    {
        var hash = default(HashCode);
        foreach (var span in schedule)
        {
            hash.Add(span);
        }

        return hash.ToHashCode();
    }

    private static TimeSpan[] Snapshot(IReadOnlyList<TimeSpan> schedule)
    {
        return [.. schedule];
    }
}

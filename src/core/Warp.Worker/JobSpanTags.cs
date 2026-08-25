using System.Diagnostics;
using Warp.Core.Logging;

namespace Warp.Worker;

internal static class JobSpanTags
{
    // Read AFTER the pipeline ran, not from the fetched row: nothing is stamped at publish (§8.8), so the
    // retry budget exists in metadata only once RetryPipelineBehavior resolved it during the attempt.
    // Freshly stamped values are ints; round-tripped ones deserialize as longs.
    public static void SetMaxAttempts(Activity? activity, IReadOnlyDictionary<string, object> metadata)
    {
        if (activity == null)
        {
            return;
        }

        if (!metadata.TryGetValue(WarpTelemetryAttributes.RetryMetadataMaxRetriesKey, out var value))
        {
            return;
        }

        if (value is int or long)
        {
            activity.SetTag(WarpTelemetryAttributes.WarpJobMaxAttempts, Convert.ToInt64(value) + 1);
        }
    }
}

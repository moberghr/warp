namespace Warp.Core;

/// <summary>
/// Canonicalizes the recurring-job name — the identity every public API keys on
/// (<see cref="IRecurringJobPublisher.AddOrUpdateRecurringJob{T}"/> registers by it,
/// <see cref="Warp.Core.Services.IRecurringJobService"/> triggers/enables/deletes by it, and the
/// dashboard addresses a definition by its URL-safe encoding).
/// </summary>
/// <remarks>
/// One normalizer shared by the write and read sides so a lookup can never miss a definition over
/// surrounding whitespace. Deliberately NOT a column constraint: RecurringJob.Name has no
/// HasMaxLength, so tightening it here needs no consumer migration (§5.12 — Warp owns its column
/// storage, and changing that column's facets would generate one).
/// </remarks>
internal static class RecurringJobName
{
    // Matches SagaCorrelationKeyConverter.MaxKeyLength for the same two reasons: the name is the
    // identity a distributed lock is named after ("warp:recurring:{name}"), and an unbounded
    // identity makes an ambiguous lock name and an unwieldy dashboard route segment.
    public const int MaxNameLength = 200;

    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var normalized = name.Trim();

        if (normalized.Length == 0)
        {
            throw new ArgumentException(
                "Recurring job name is empty. The name is the definition's identity — the key " +
                "AddOrUpdateRecurringJob registers under and every trigger/enable/disable/delete " +
                "call looks up by.",
                nameof(name));
        }

        if (normalized.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Recurring job name length ({normalized.Length}) exceeds the maximum of " +
                $"{MaxNameLength}. The name identifies the definition and names its distributed " +
                $"lock ('warp:recurring:{{name}}'); use a shorter identifier.",
                nameof(name));
        }

        return normalized;
    }
}

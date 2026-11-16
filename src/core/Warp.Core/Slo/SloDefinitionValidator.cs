using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;

namespace Warp.Core.Slo;

/// <summary>
/// The one place an SLO definition is validated against the live instrument registry, shared by the dashboard upsert
/// endpoint and the config seeder so the two cannot drift. A metric kind looks up the registry kind of its instrument
/// and of a gap's secondary instrument (one single-row read each), so a gauge — which has no event count — is refused
/// in either role; an instrument not in the registry yet is allowed. Everything else is
/// <see cref="SloValidation.TryValidate(SloDefinition, TimeSpan?, MetricKind?, MetricKind?, out string?)"/>.
/// </summary>
public static class SloDefinitionValidator
{
    /// <summary>Validates <paramref name="definition"/>; on failure the error is a caller-facing message.</summary>
    public static async Task<(bool Ok, string? Error)> ValidateAsync(
        SloDefinition definition,
        IMetricQueryService metrics,
        TimeSpan? hourlyStatisticsRetention,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(metrics);

        MetricKind? instrumentKind = null;
        MetricKind? secondaryInstrumentKind = null;
        if (SloValidation.IsMetricKind(definition.Kind))
        {
            if (!string.IsNullOrEmpty(definition.Dimension))
            {
                instrumentKind = await metrics.GetInstrumentKindAsync(definition.Dimension, ct);
            }

            if (!string.IsNullOrEmpty(definition.SecondaryMetric))
            {
                secondaryInstrumentKind = await metrics.GetInstrumentKindAsync(definition.SecondaryMetric, ct);
            }
        }

        var ok = SloValidation.TryValidate(definition, hourlyStatisticsRetention, instrumentKind, secondaryInstrumentKind, out var error);

        return (ok, error);
    }
}

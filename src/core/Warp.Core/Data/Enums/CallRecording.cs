namespace Warp.Core.Enums;

/// <summary>
/// Controls whether an <c>AdapterCallLog</c> row is written for a completed adapter call.
/// Decoupled from capture (which controls payload richness only): <see cref="All"/> writes a
/// row per call including successes; <see cref="FailuresOnly"/> is the volume knob for hot
/// adapters. Counters and telemetry are emitted regardless.
/// </summary>
public enum CallRecording
{
    All = 1,
    FailuresOnly = 2,
}

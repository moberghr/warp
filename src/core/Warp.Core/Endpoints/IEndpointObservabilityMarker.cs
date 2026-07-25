namespace Warp.Core.Endpoints;

/// <summary>
/// Presence marker for opt-in inbound endpoint observability. Registered by
/// <c>AddEndpointObservability()</c> regardless of the recording sink, so the dashboard "endpoints" nav
/// flag reports true even under <c>RecordingSink.Otel</c> (where no <see cref="IEndpointCallRecorder"/> is
/// registered because the call detail rides the request span, §8.24). Mirrors <c>IAdapterRecordingMarker</c>.
/// </summary>
public interface IEndpointObservabilityMarker;

/// <summary>Default marker implementation (public because <c>AddEndpointObservability</c> lives in <c>Warp.Http</c>).</summary>
public sealed class EndpointObservabilityMarker : IEndpointObservabilityMarker;

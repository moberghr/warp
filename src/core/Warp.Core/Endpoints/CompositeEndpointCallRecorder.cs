namespace Warp.Core.Endpoints;

/// <summary>
/// Fans a completed inbound endpoint request to both the DB recorder and the OTel recorder — the
/// <c>RecordingSink.Both</c> wiring, mirror of <c>CompositeAdapterCallRecorder</c>. Both recorders are
/// always invoked (no short-circuit) so a full DB channel never suppresses the OTLP log and vice versa;
/// <see cref="Record"/> returns <c>true</c> if either accepted. Neither delegate throws, so this never
/// throws. Public to mirror the public <see cref="DbEndpointCallRecorder"/> so the <c>Warp.Http</c>
/// binding can register it.
/// </summary>
public sealed class CompositeEndpointCallRecorder : IEndpointCallRecorder
{
    private readonly IEndpointCallRecorder _database;
    private readonly IEndpointCallRecorder _otel;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeEndpointCallRecorder"/> class.
    /// </summary>
    public CompositeEndpointCallRecorder(IEndpointCallRecorder database, IEndpointCallRecorder otel)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(otel);
        _database = database;
        _otel = otel;
    }

    /// <inheritdoc />
    public bool Record(EndpointCallRecord record)
    {
        var acceptedByDatabase = _database.Record(record);
        var acceptedByOtel = _otel.Record(record);

        return acceptedByDatabase || acceptedByOtel;
    }
}

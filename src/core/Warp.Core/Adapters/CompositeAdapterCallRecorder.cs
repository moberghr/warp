namespace Warp.Core.Adapters;

/// <summary>
/// Fans a completed adapter call to both the DB recorder and the OTel recorder — the
/// <c>RecordingSink.Both</c> wiring. Both recorders are always invoked (no short-circuit) so a full DB
/// channel never suppresses the OTLP log and vice versa; <see cref="Record"/> returns <c>true</c> if
/// either recorder accepted the record. Neither delegate throws (the DB recorder is a non-blocking
/// <c>TryWrite</c>; the OTel recorder swallows logging failures), so this never throws.
/// </summary>
internal sealed class CompositeAdapterCallRecorder : IAdapterCallRecorder
{
    private readonly IAdapterCallRecorder _database;
    private readonly IAdapterCallRecorder _otel;

    public CompositeAdapterCallRecorder(IAdapterCallRecorder database, IAdapterCallRecorder otel)
    {
        _database = database;
        _otel = otel;
    }

    public bool Record(AdapterCallRecord record)
    {
        var acceptedByDatabase = _database.Record(record);
        var acceptedByOtel = _otel.Record(record);

        return acceptedByDatabase || acceptedByOtel;
    }
}

using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Handler that writes a key to metadata during execution.
/// After completion, the test can verify this key was persisted.
/// Carries <c>[Retry]</c> so <c>PolicyResolver</c> stamps <c>MaxRetries</c> into metadata at the job's
/// first execution (§8.8) — the metadata-propagation tests assert that an addon-stamped key and a
/// handler-written key both survive to completion.
/// </summary>
[Retry(3)]
public class MetadataWriterRequest : IJob;

public class MetadataWriterHandler(IJobContext ctx) : IJobHandler<MetadataWriterRequest>
{
    public Task HandleAsync(MetadataWriterRequest message, CancellationToken cancellationToken)
    {
        ctx.Metadata["HandlerWrote"] = "from-handler";

        return Task.CompletedTask;
    }
}

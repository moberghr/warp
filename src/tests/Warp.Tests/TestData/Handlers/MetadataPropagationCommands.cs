using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

/// <summary>
/// Handler that writes a key to metadata during execution.
/// After completion, the test can verify this key was persisted.
/// Carries <c>[Retry]</c> so the publish pipeline stamps <c>MaxRetries</c> into metadata at publish
/// (the metadata-propagation tests assert that publish-pipeline metadata survives to completion).
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

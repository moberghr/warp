using Moq;
using Shouldly;
using Warp.Core.Models;
using Warp.Core.Services;

namespace Warp.Tests.Admin;

[Trait("Category", "NoDb")]
public class TraceGraphDefaultTests
{
    [TimedFact]
    public async Task GetTraceGraph_ImplementationWithoutOverride_TrimsGetTraceTree()
    {
        // A host implementation written before GetTraceGraph existed only has GetTraceTree; the
        // interface's default must still cap and flag it.
        var traceId = Guid.NewGuid();
        var service = new Mock<IJobQueryService> { CallBase = true };
        service
            .Setup(x => x.GetTraceTree(traceId))
            .ReturnsAsync([.. Enumerable.Range(0, TraceGraphModel.MaxJobs + 2).Select(_ => new TraceJobModel { Id = Guid.NewGuid() })]);

        var graph = await service.Object.GetTraceGraph(traceId);

        graph.Jobs.Count.ShouldBe(TraceGraphModel.MaxJobs);
        graph.IsTruncated.ShouldBeTrue();
    }
}

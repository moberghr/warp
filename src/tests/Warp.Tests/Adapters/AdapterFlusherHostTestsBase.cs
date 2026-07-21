using Microsoft.EntityFrameworkCore;
using Shouldly;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Adapters;

/// <summary>
/// Boots the REAL <c>AddAdapters()</c> DI wiring — bounded channel, <c>AdapterCallFlusher</c> hosted
/// service, scope-factory persistence — and proves a completed scope lands as an <see cref="AdapterCallLog"/>
/// row without any hand-built internals (adapters lesson: tests that construct internal seams verify the
/// seam, not the wiring). The second call proves the drain loop KEEPS running after its first batch — a
/// loop-exit regression would silently stop all adapter recording in production with a green suite.
/// </summary>
[GenerateDatabaseTests]
public abstract class AdapterFlusherHostTestsBase : IntegrationTestBase
{
    protected AdapterFlusherHostTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Flusher_BootedThroughRealDi_PersistsRecordedCallsAcrossBatches()
    {
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg => cfg.AddAdapters());

        var adapters = server.GetService<IWarpAdapters>();

        adapters.BeginCall("vendor", "GetOrders").Succeed();

        await WaitForCallLogCountAsync(server, 1);

        // A second call AFTER the first batch persisted exercises the next drain-loop iteration.
        using (var scope = adapters.BeginCall("vendor", "GetOrders"))
        {
            scope.Fail(new InvalidOperationException("boom"));
        }

        await WaitForCallLogCountAsync(server, 2);

        var outcomes = await server.CreateContext().Set<AdapterCallLog>()
            .Where(x => x.AdapterName == "vendor")
            .Select(x => x.Outcome)
            .ToListAsync(Ct);

        outcomes.ShouldContain(AdapterCallOutcome.Success);
        outcomes.ShouldContain(AdapterCallOutcome.Failed);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static Task WaitForCallLogCountAsync(WarpTestServer server, int count)
    {
        return WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .CountAsync(x => x.AdapterName == "vendor", Ct) >= count,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }
}

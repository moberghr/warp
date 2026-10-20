using Shouldly;
using Warp.Core;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Tests.Fixtures;

namespace Warp.Tests.Admin;

/// <summary>
/// <c>Page * PageSize</c> overflowed <see cref="int"/> for a large page from the query string, giving a
/// negative OFFSET that both providers reject with a 500. A page past the end must read as empty.
/// </summary>
[GenerateDatabaseTests]
public abstract class PagingOverflowTestsBase : IAsyncLifetime
{
    private static readonly BaseListRequest FarPastTheEnd = new() { Page = 4_294_968, PageSize = 500 };

    private readonly IDatabaseFixture _fixture;

    protected PagingOverflowTestsBase(IDatabaseFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync() => await _fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [TimedFact]
    public async Task ToPagedListAsync_PageFarPastTheEnd_ReturnsEmptyPage()
    {
        var svc = new JobQueryService<TestContext>(_fixture.CreateContext(), TimeProvider.System);

        var page = await svc.GetJobsList(FarPastTheEnd, State.Completed);

        page.Items.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task ToPagedSliceAsync_PageFarPastTheEnd_ReturnsEmptySlice()
    {
        var svc = new JobLogQueryService<TestContext>(_fixture.CreateContext());

        var page = await svc.GetHandlerLogs(Guid.NewGuid(), FarPastTheEnd);

        page.Items.ShouldBeEmpty();
        page.HasMore.ShouldBeFalse();
    }
}

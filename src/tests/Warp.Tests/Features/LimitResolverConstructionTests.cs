using Moq;
using Shouldly;
using Warp.Core.Concurrency;
using Warp.Core.RateLimit;

namespace Warp.Tests.Features;

// The pipeline behaviours' public constructors take these resolvers, so a host that builds a behaviour
// itself must be able to build its resolver: from the manager whose admin rows it should read.
[Trait("Category", "NoDb")]
public sealed class LimitResolverConstructionTests
{
    [TimedFact]
    public async Task RateLimitResolver_BuiltFromAManager_ReadsTheOverrideFromIt()
    {
        var info = new RateLimitInfo("orders", 5, 60, DateTime.UtcNow);
        var manager = new Mock<IRateLimitManager>();
        manager.Setup(x => x.GetLimit("orders", It.IsAny<CancellationToken>())).ReturnsAsync(info);
        var resolver = new RateLimitResolver(manager.Object);

        var resolved = await resolver.GetOverride("orders", Xunit.TestContext.Current.CancellationToken);

        resolved.ShouldBe(info);
    }

    [TimedFact]
    public async Task ConcurrencyLimitResolver_BuiltFromAManager_ReadsTheLimitFromIt()
    {
        var manager = new Mock<IConcurrencyLimitManager>();
        manager.Setup(x => x.GetLimit("orders", It.IsAny<CancellationToken>())).ReturnsAsync(new ConcurrencyLimitInfo("orders", 3, DateTime.UtcNow));
        var resolver = new ConcurrencyLimitResolver(manager.Object);

        var resolved = await resolver.GetLimit("orders", Xunit.TestContext.Current.CancellationToken);

        resolved.ShouldBe(3);
    }
}

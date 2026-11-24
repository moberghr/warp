using Shouldly;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Worker;

[Trait("Category", "NoDb")]
public class JobTypesTests
{
    [TimedFact]
    public void Resolve_AStoredAssemblyQualifiedName_ReturnsItsType()
    {
        // The name a job row stores; resolved once per process and served from the cache after.
        var name = typeof(UnitRequest).AssemblyQualifiedName!;

        var first = JobTypes.Resolve(name);
        var second = JobTypes.Resolve(name);

        first.ShouldBe(typeof(UnitRequest));
        second.ShouldBeSameAs(first);
    }

    [TimedFact]
    public void Resolve_ANameThatDoesNotResolve_ReturnsNull()
    {
        // The caller fails the job with "Unknown type"; nothing is cached for it.
        JobTypes.Resolve("Warp.Tests.NoSuchType, Warp.Tests").ShouldBeNull();
    }
}

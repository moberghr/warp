using Shouldly;
using Warp.Core;

namespace Warp.Tests.Core;

[Trait("Category", "NoDb")]
public class WarpVersionTests
{
    /// <summary>
    /// SourceLink appends <c>+{commit sha}</c> to the informational version; the dashboard shows a release number,
    /// so the build metadata is dropped and the rest is kept verbatim (prerelease tags included).
    /// </summary>
    [TimedTheory]
    [InlineData("7.1.0+221d0bb9fe7d96895c75a98a520430323e0c1ccb", "7.1.0")]
    [InlineData("7.2.0-rc.1+221d0bb9", "7.2.0-rc.1")]
    [InlineData("7.1.0", "7.1.0")]
    public void Resolve_InformationalVersion_StripsBuildMetadataOnly(string informational, string expected)
    {
        var version = WarpVersion.Resolve(informational, "7.1.0.0");

        version.ShouldBe(expected);
    }

    [TimedFact]
    public void Resolve_NoInformationalVersion_FallsBackToFileVersion()
    {
        var version = WarpVersion.Resolve(null, "7.2.3.0");

        version.ShouldBe("7.2.3.0");
    }

    [TimedFact]
    public void Resolve_NoVersionAttributes_ReturnsUnknown()
    {
        var version = WarpVersion.Resolve(null, null);

        version.ShouldBe("unknown");
    }
}

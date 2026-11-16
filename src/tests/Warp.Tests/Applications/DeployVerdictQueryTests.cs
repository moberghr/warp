using Shouldly;
using Warp.Core.Services;

namespace Warp.Tests.Applications;

/// <summary>NoDb coverage for the verdict route's input rules (<see cref="DeployVerdictQuery.TryCreate"/>), the source of its 400s.</summary>
[Trait("Category", "NoDb")]
public class DeployVerdictQueryTests
{
    [TimedFact]
    public void TryCreate_OnlyApplication_UsesTheDefaults()
    {
        DeployVerdictQuery.TryCreate(" api ", " ", null, null, out var query, out _).ShouldBeTrue();

        query.Application.ShouldBe("api");
        query.Version.ShouldBeNull();
        query.Environment.ShouldBeNull();
        query.MinWindow.ShouldBe(TimeSpan.FromMinutes(DeployVerdictQuery.DefaultMinWindowMinutes));
    }

    [TimedTheory]
    [InlineData("0", 0)]
    [InlineData("15", 15)]
    [InlineData("1440", 1440)]
    public void TryCreate_MinWindowInRange_IsAccepted(string minutes, int expected)
    {
        DeployVerdictQuery.TryCreate("api", "2.0.0", "prod", minutes, out var query, out _).ShouldBeTrue();

        query.MinWindow.ShouldBe(TimeSpan.FromMinutes(expected));
        query.Version.ShouldBe("2.0.0");
        query.Environment.ShouldBe("prod");
    }

    [TimedTheory]
    [InlineData(null, null, "application")]
    [InlineData("  ", null, "application")]
    [InlineData("api", "-1", "minWindowMinutes")]
    [InlineData("api", "1441", "minWindowMinutes")]
    [InlineData("api", "5.5", "minWindowMinutes")]
    [InlineData("api", "soon", "minWindowMinutes")]
    public void TryCreate_BadInput_IsRejectedNamingTheParameter(string? application, string? minutes, string parameter)
    {
        DeployVerdictQuery.TryCreate(application, null, null, minutes, out _, out var error).ShouldBeFalse();

        error.ShouldContain(parameter);
    }

    [TimedFact]
    public void TryCreate_OverlongVersion_IsRejected()
    {
        DeployVerdictQuery.TryCreate("api", new string('v', DeployVerdictQuery.MaxNameLength + 1), null, null, out _, out var error).ShouldBeFalse();

        error.ShouldContain("at most");
    }
}

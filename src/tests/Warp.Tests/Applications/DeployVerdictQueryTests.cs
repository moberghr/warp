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
        DeployVerdictQuery.TryCreate(" api ", " ", null, null, " ", out var query, out _).ShouldBeTrue();

        query.Application.ShouldBe("api");
        query.Version.ShouldBeNull();
        query.Environment.ShouldBeNull();
        query.MinWindow.ShouldBe(TimeSpan.FromMinutes(DeployVerdictQuery.DefaultMinWindowMinutes));
        query.MinCoverage.ShouldBe(0);
    }

    [TimedTheory]
    [InlineData("0", 0)]
    [InlineData("0.5", 0.5)]
    [InlineData(".25", 0.25)]
    [InlineData("1", 1)]
    public void TryCreate_MinCoverageInRange_IsAccepted(string minCoverage, double expected)
    {
        DeployVerdictQuery.TryCreate("api", null, null, null, minCoverage, out var query, out _).ShouldBeTrue();

        query.MinCoverage.ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("-0.1")]
    [InlineData("1.01")]
    [InlineData("2")]
    [InlineData("NaN")]
    [InlineData("1e-1")]
    [InlineData("half")]
    public void TryCreate_BadMinCoverage_IsRejectedNamingTheParameter(string minCoverage)
    {
        DeployVerdictQuery.TryCreate("api", null, null, null, minCoverage, out _, out var error).ShouldBeFalse();

        error.ShouldBe("'minCoverage' must be a number from 0 to 1.");
    }

    [TimedTheory]
    [InlineData("0", 0)]
    [InlineData("15", 15)]
    [InlineData("1440", 1440)]
    public void TryCreate_MinWindowInRange_IsAccepted(string minutes, int expected)
    {
        DeployVerdictQuery.TryCreate("api", "2.0.0", "prod", minutes, null, out var query, out _).ShouldBeTrue();

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
        DeployVerdictQuery.TryCreate(application, null, null, minutes, null, out _, out var error).ShouldBeFalse();

        error.ShouldContain(parameter);
    }

    [TimedFact]
    public void TryCreate_OverlongVersion_IsRejected()
    {
        DeployVerdictQuery.TryCreate("api", new string('v', DeployVerdictQuery.MaxNameLength + 1), null, null, null, out _, out var error).ShouldBeFalse();

        error.ShouldContain("at most");
    }

    [TimedTheory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" Checkout ", "Checkout")]
    public void TryCreate_Board_IsTrimmedAndBlankMeansEveryBoard(string? board, string? expected)
    {
        DeployVerdictQuery.TryCreate("api", null, null, null, null, board, out var query, out _).ShouldBeTrue();

        query.Board.ShouldBe(expected);
    }

    [TimedFact]
    public void TryCreate_OverlongBoard_IsRejected()
    {
        DeployVerdictQuery.TryCreate("api", null, null, null, null, new string('b', 201), out _, out var error).ShouldBeFalse();

        error.ShouldContain("'board'");
    }
}

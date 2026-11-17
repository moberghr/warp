using Shouldly;
using Warp.Core.Data.Entities;
using Warp.Core.SavedViews;

namespace Warp.Tests.SavedViews;

/// <summary><see cref="SavedViewValidation"/>, one row per branch: the message a 400 carries, and the normalization.</summary>
[Trait("Category", "NoDb")]
public class SavedViewValidationTests
{
    [TimedTheory]
    [InlineData("", "metric", "c", "", "Name is required and must be at most 200 characters.")]
    [InlineData("long-name", "metric", "c", "", "Name is required and must be at most 200 characters.")]
    [InlineData("v", "webhooks", null, "", "Page must be one of: metric, issues, jobs, jobs-by-type.")]
    [InlineData("v", "Metric", "c", "", "Page must be one of: metric, issues, jobs, jobs-by-type.")]
    [InlineData("v", "metric", null, "", "A metric view needs a target: the instrument name.")]
    [InlineData("v", "metric", "  ", "", "A metric view needs a target: the instrument name.")]
    [InlineData("v", "jobs", null, "", "A jobs view needs a target: the job state.")]
    [InlineData("v", "jobs-by-type", null, "", "A jobs-by-type view needs a target: the job type.")]
    [InlineData("v", "issues", "x", "", "An issues view has no target.")]
    [InlineData("v", "jobs", "stuck", "", "Target must be a job state: enqueued, processing, scheduled, retrying, awaiting, completed, failed, deleted.")]
    [InlineData("v", "metric", "long-target", "", "Target must be at most 256 characters.")]
    [InlineData("v", "metric", "c", "long-query", "Query must be at most 2000 characters.")]
    [InlineData("v", "metric", "c", "range=7d&=x", "Query holds a parameter without a name.")]
    [InlineData("v", "issues", null, "status=resolved&next=https%3A%2F%2Fevil", "Query parameter 'next' is not read by the issues page. Known parameters: source, status, application, show4xx.")]
    [InlineData("v", "jobs", "failed", "metrics=handler", "Query parameter 'metrics' is not read by the jobs page. Known parameters: page, type.")]
    [InlineData("v", "jobs-by-type", "T", "type=T", "Query parameter 'type' is not read by the jobs-by-type page. Known parameters: page, metrics.")]
    [InlineData("v", "metric", "c", "r%61nge=7d&utm=1", "Query parameter 'utm' is not read by the metric page. Known parameters: range, from, to, application, breakdown, filter, compare, percentile, view, numerator, minmax, rate.")]
    public void TryValidate_Invalid_ReturnsTheMessage(string name, string page, string? target, string query, string expected)
    {
        var view = new SavedView
        {
            Name = string.Equals(name, "long-name", StringComparison.Ordinal) ? new string('n', SavedViewValidation.MaxNameLength + 1) : name,
            Page = page,
            Target = string.Equals(target, "long-target", StringComparison.Ordinal) ? new string('t', SavedViewValidation.MaxTargetLength + 1) : target,
            Query = string.Equals(query, "long-query", StringComparison.Ordinal) ? "range=" + new string('q', SavedViewValidation.MaxQueryLength) : query,
        };

        SavedViewValidation.TryValidate(view, out var error).ShouldBeFalse();

        error.ShouldBe(expected);
    }

    [TimedTheory]
    [InlineData("metric", "shop.checkout.completed", "range=7d&breakdown=%7Bversion%7D&filter=outcome%3Aerror&filter=%7Bversion%7D%3A2.*&compare=1d,7d&percentile=95&view=ratio&numerator=ok&minmax=1&rate=minute")]
    [InlineData("metric", "c", "from=2026-11-16T10%3A00%3A00.000Z&to=2026-11-16T12%3A00%3A00.000Z&application=shop-api")]
    [InlineData("issues", null, "source=endpoint&status=resolved&application=shop-api&show4xx=1")]
    [InlineData("jobs", "failed", "page=2&type=Orders.Charge")]
    [InlineData("jobs-by-type", "Orders.Charge", "page=1&metrics=handler")]
    [InlineData("issues", null, "")]
    public void TryValidate_KnownKeysOfEachPage_AreAccepted(string page, string? target, string query)
    {
        var view = new SavedView { Name = "v", Page = page, Target = target, Query = query };

        SavedViewValidation.TryValidate(view, out var error).ShouldBeTrue(error);

        error.ShouldBeNull();
    }

    [TimedFact]
    public void TryValidate_Normalizes_NamePageTargetAndALeadingQuestionMark()
    {
        var view = new SavedView { Name = "  Checkout  ", Page = " metric ", Target = "  c  ", Query = " ?range=7d " };

        SavedViewValidation.TryValidate(view, out _).ShouldBeTrue();

        view.Name.ShouldBe("Checkout");
        view.Page.ShouldBe("metric");
        view.Target.ShouldBe("c");
        view.Query.ShouldBe("range=7d");
    }

    [TimedFact]
    public void TryValidate_BlankTargetOnIssues_IsCleared()
    {
        var view = new SavedView { Name = "v", Page = "issues", Target = "  ", Query = string.Empty };

        SavedViewValidation.TryValidate(view, out _).ShouldBeTrue();

        view.Target.ShouldBeNull();
    }

    [TimedFact]
    public void ParseKeys_DecodesNamesAndKeepsRepeats()
    {
        SavedViewValidation.ParseKeys("filter=a%3Ab&filter=c%3Ad&r%61nge=7d&minmax&&a+b=1").ShouldBe(["filter", "filter", "range", "minmax", "a b"]);
    }
}

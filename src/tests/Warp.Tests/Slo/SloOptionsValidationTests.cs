using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.Services;
using Warp.Core.Slo;
using Warp.Dashboard;
using Warp.Dashboard.Endpoints;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.Observability;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Slo;

// AddSlo's registration-time checks on the options object: nothing is read from or written to a database.
// Moved out of SloCommandQueryTestsBase, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class SloOptionsValidationTests
{
    [TimedFact]
    public void AddMetric_SameKindNameAndApplicationTwice_Throws()
    {
        var options = new SloOptions().AddMetricThreshold("5xx", "a", 0);

        Should.Throw<ArgumentException>(() => options.AddMetricThreshold("5xx", "b", 0));
    }

    [TimedTheory]
    [InlineData("5XX", null)]
    [InlineData("5xx", "API")]
    public void AddMetric_NameOrApplicationDifferingOnlyByCase_Throws(string name, string? application)
    {
        // The seeder matches names case-insensitively (in memory, on every provider): these would be one objective.
        var options = new SloOptions().AddMetricThreshold("5xx", "a", 0, application: application is null ? null : "api");

        Should.Throw<ArgumentException>(() => options.AddMetricThreshold(name, "b", 0, application: application));
    }

    [TimedFact]
    public void AddMetricLatency_InvalidPercentileOrDuplicateName_Throws()
    {
        Should.Throw<ArgumentException>(() => new SloOptions().AddMetricLatency("p", "h", 1, percentile: 100));

        var options = new SloOptions().AddMetricLatency("p95", "h", 1);
        Should.Throw<ArgumentException>(() => options.AddMetricLatency("p95", "h", 2));
    }
}

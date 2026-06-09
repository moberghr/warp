using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Tests.TestData;
using Warp.Worker;

namespace Warp.Tests.Core;

// Covers BindConfiguration — the appsettings.json bridge on the Warp builder. Uses a
// MemoryConfigurationSource so the test is pure-unit (no appsettings.json on disk). Builder
// inherits WarpConfiguration fields, so we assert via the IWarpBuilder.Configuration
// surface (the same instance) and via the worker-specific fields that live on the concrete
// builder type.
[Trait("Category", "NoDb")]
public class WarpBuilderExtensionsTests
{
    [Fact]
    public void BindConfiguration_PopulatesFieldsFromSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Warp:DefaultQueue"] = "custom",
                ["Warp:Schema"] = "myschema",
                ["Warp:JobExpirationTimeout"] = "02:00:00",
            })
            .Build();

        var builder = new WarpBuilder<TestContext>(new ServiceCollection());

        builder.BindConfiguration(config.GetSection("Warp"));

        builder.DefaultQueue.ShouldBe("custom");
        builder.Schema.ShouldBe("myschema");
        builder.JobExpirationTimeout.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void BindConfiguration_UnknownKeys_AreIgnored()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Warp:DefaultQueue"] = "custom",
                ["Warp:NotARealField"] = "ignored",
            })
            .Build();

        var builder = new WarpBuilder<TestContext>(new ServiceCollection());

        Should.NotThrow(() => builder.BindConfiguration(config.GetSection("Warp")));
        builder.DefaultQueue.ShouldBe("custom");
    }

    [Fact]
    public void BindConfiguration_ReturnsSameBuilderInstance()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([])
            .Build();

        var builder = new WarpBuilder<TestContext>(new ServiceCollection());
        IWarpBuilder<TestContext> asInterface = builder;

        var returned = asInterface.BindConfiguration(config.GetSection("Warp"));

        returned.ShouldBeSameAs(builder);
    }

    // Pins that BindConfiguration hits worker-specific fields on the worker builder. The
    // appsettings.json flow is the primary use case, and most of its value is setting
    // WorkerCount / PollingInterval / HealthCheckTimeout, which only exist on the worker
    // subclass — not on the base WarpConfiguration.
    [Fact]
    public void BindConfiguration_PopulatesWorkerFields()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Warp:WorkerCount"] = "7",
                ["Warp:PollingInterval"] = "00:00:05",
                ["Warp:HealthCheckTimeout"] = "00:10:00",
            })
            .Build();

        var builder = new WarpServerBuilder<TestContext>(new ServiceCollection());

        builder.BindConfiguration(config.GetSection("Warp"));

        builder.WorkerCount.ShouldBe(7);
        builder.PollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
        builder.HealthCheckTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }
}

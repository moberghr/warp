using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Warp.Core;
using Warp.Core.Data;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Handlers;
using Warp.Core.Handlers.Generated;
using Warp.Core.Helper;
using Warp.Core.RateLimit;
using Warp.Core.Services;
using Warp.Tests.Fixtures;
using Warp.Tests.Helpers;
using Warp.Tests.TestData.Handlers;
using Warp.Worker;

namespace Warp.Tests.Features.RateLimit;

// [RateLimit]'s constructor arguments are validated when the attribute is built: no worker and no database involved.
// Moved out of RateLimitTests, where each of these ran once per database for no reason.
[Trait("Category", "NoDb")]
public class RateLimitAttributeValidationTests
{
    [TimedFact]
    public Task RateLimitAttribute_EmptyKey_ThrowsAtConstruction()
    {
        Should.Throw<ArgumentException>(() => new RateLimitAttribute(string.Empty, 1, 60));
        Should.Throw<ArgumentNullException>(() => new RateLimitAttribute(null!, 1, 60));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_ZeroCount_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 0, 60));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_ZeroPerSeconds_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 1, 0));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task WithRateLimit_WindowExceedsMaxWindow_Throws()
    {
        // Same cap as the attribute: the stale-bucket sweep relies on no window being longer.
        Should.Throw<ArgumentOutOfRangeException>(() => new JobParameters().WithRateLimit("k", 1, TimeSpan.FromSeconds(RateLimitAttribute.MaxWindowSeconds + 1)));
        Should.NotThrow(() => new JobParameters().WithRateLimit("k", 1, TimeSpan.FromSeconds(RateLimitAttribute.MaxWindowSeconds)));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_PerSecondsExceedsMaxWindow_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new RateLimitAttribute("k", 1, RateLimitAttribute.MaxWindowSeconds + 1));

        return Task.CompletedTask;
    }

    [TimedFact]
    public Task RateLimitAttribute_PerSecondsAtMaxWindow_DoesNotThrow()
    {
        var attr = new RateLimitAttribute("k", 1, RateLimitAttribute.MaxWindowSeconds);

        attr.PerSeconds.ShouldBe(RateLimitAttribute.MaxWindowSeconds);

        return Task.CompletedTask;
    }
}

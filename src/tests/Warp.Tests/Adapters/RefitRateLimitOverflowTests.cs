using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Refit;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Adapters.Refit;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Tests.Adapters;

/// <summary>
/// The Refit seam a shared-rate-limited adapter runs into (#284): Refit wraps EVERY exception escaping the
/// <see cref="HttpClient"/> pipeline in <c>ApiRequestException</c>, so <c>catch (AdapterRateLimitedException)</c>
/// silently never fires for a Refit-bound adapter, and a method returning <c>ApiResponse&lt;T&gt;</c> does not
/// throw at all. These tests pin that wrapping (so a Refit upgrade that changes it is caught), and cover the
/// two answers: <see cref="AdapterRateLimitOverflow.Respond429"/> — the refusal arrives as an ordinary 429
/// on both Refit shapes — and <see cref="AdapterRateLimitExtensions.IsAdapterRateLimited"/> for callers who
/// stay on the throwing modes.
/// </summary>
[Trait("Category", "NoDb")]
public class RefitRateLimitOverflowTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(2500);

    [TimedFact]
    public async Task FailFast_RefitCall_ArrivesWrapped_AndIsUnreachableByType()
    {
        // The defect itself, pinned: the natural catch does not fire, so a caller cannot tell "we throttled
        // ourselves" from "the vendor is broken" by type alone.
        var (provider, transport, _) = BuildRefitAdapter(AdapterRateLimitOverflow.FailFast);

        await using (provider)
        {
            var caught = await Should.ThrowAsync<Exception>(async () =>
                await provider.GetRequiredService<IThrottledApi>().GetThing(1));

            caught.ShouldBeOfType<ApiRequestException>();
            (caught is AdapterRateLimitedException).ShouldBeFalse();
            caught.InnerException.ShouldBeOfType<AdapterRateLimitedException>();
            transport.Invocations.ShouldBe(0);
        }
    }

    [TimedFact]
    public async Task FailFast_RefitCall_IsRecognisedByTheChainWalkingHelper()
    {
        var (provider, _, _) = BuildRefitAdapter(AdapterRateLimitOverflow.FailFast);

        await using (provider)
        {
            var caught = await Should.ThrowAsync<Exception>(async () =>
                await provider.GetRequiredService<IThrottledApi>().GetThing(1));

            caught.IsAdapterRateLimited().ShouldBeTrue();
            caught.GetAdapterRetryAfter().ShouldBe(Wait);
        }
    }

    [TimedFact]
    public async Task Respond429_RefitCall_SurfacesAsApiExceptionWithRetryAfter()
    {
        var (provider, transport, _) = BuildRefitAdapter(AdapterRateLimitOverflow.Respond429);

        await using (provider)
        {
            var caught = await Should.ThrowAsync<ApiException>(async () =>
                await provider.GetRequiredService<IThrottledApi>().GetThing(1));

            caught.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

            // Retry-After is whole seconds on the wire, rounded UP from the limiter's 2.5s.
            (caught.Headers?.RetryAfter?.Delta).ShouldBe(TimeSpan.FromSeconds(3));
            transport.Invocations.ShouldBe(0);
        }
    }

    [TimedFact]
    public async Task Respond429_RefitApiResponseMethod_ReturnsThrottledResultWithoutThrowing()
    {
        // The second half of #284: an ApiResponse<T> method never throws, so an exception-based refusal is
        // invisible on that shape. A 429 lands on the result like any other non-success status.
        var (provider, _, _) = BuildRefitAdapter(AdapterRateLimitOverflow.Respond429);

        await using (provider)
        {
            using var response = await provider.GetRequiredService<IThrottledApi>().GetThingResponse(1);

            response.ShouldNotBeNull();
            response.IsSuccessStatusCode.ShouldBeFalse();
            response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            (response.Headers?.RetryAfter?.Delta).ShouldBe(TimeSpan.FromSeconds(3));
        }
    }

    [TimedFact]
    public async Task Respond429_RecordsThrottledOutcome_NotFailed()
    {
        // The classification the mode exists to preserve: a synthetic 429 is a non-success status, so
        // without the marker the outermost handler would record it Failed like any other 4xx.
        var (provider, transport, recorder) = BuildRefitAdapter(AdapterRateLimitOverflow.Respond429);

        await using (provider)
        {
            await Should.ThrowAsync<ApiException>(async () =>
                await provider.GetRequiredService<IThrottledApi>().GetThing(1));

            var record = recorder.Records.ShouldHaveSingleItem();
            record.Outcome.ShouldBe(AdapterCallOutcome.Throttled);
            record.Operation.ShouldBe(nameof(IThrottledApi.GetThing));
            record.StatusCode.ShouldBe((int)HttpStatusCode.TooManyRequests);
            transport.Invocations.ShouldBe(0);
        }
    }

    private static (ServiceProvider Provider, CountingStubHandler Transport, CapturingRecorder Recorder) BuildRefitAdapter(
        AdapterRateLimitOverflow overflow)
    {
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: "throttled");
        var transport = new CountingStubHandler();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWarpAdapters>(adapters);
        services.AddSingleton<IAdapterRateLimiter>(new RejectingRateLimiter(Wait));

        new WarpBuilder<TestContext>(services).AddAdapter<IThrottledApi>("throttled", a =>
        {
            a.BaseUrl = new Uri("https://api.throttled.test");
            a.UseSharedRateLimit(1, 60, overflow, maxWait: TimeSpan.Zero);
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => transport));
        });

        return (services.BuildServiceProvider(), transport, recorder);
    }
}

/// <summary>A Refit interface covering both shapes that matter to #284: throwing and <c>ApiResponse</c>.</summary>
public interface IThrottledApi
{
    [Get("/things/{id}")]
    Task<string> GetThing(int id);

    [Get("/things/{id}")]
    Task<ApiResponse<string>> GetThingResponse(int id);
}

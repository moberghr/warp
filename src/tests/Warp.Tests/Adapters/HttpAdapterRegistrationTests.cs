using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Enums;

namespace Warp.Tests.Adapters;

/// <summary>
/// End-to-end registration coverage for <see cref="HttpAdapterServiceConfiguration.AddAdapter"/>: a real
/// <see cref="ServiceCollection"/> + <see cref="IHttpClientFactory"/> client with a stub primary handler
/// (no live network). Proves the per-adapter recording options registered at <c>AddAdapter</c> time
/// actually reach the runtime registry (F1) — <c>RecordCalls = FailuresOnly</c> takes effect and the
/// <c>EnrichCall</c> hook fires — and that the adapter-name guard rejects unusable names (F4). Not a
/// hand-built registry: everything flows through the public builder DI wiring.
/// </summary>
[Trait("Category", "NoDb")]
public class HttpAdapterRegistrationTests
{
    [TimedFact]
    public async Task AddAdapter_FailuresOnlyAndEnrichCall_TakeEffectEndToEnd()
    {
        var recorder = new CapturingRecorder();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        // Register the capturing recorder first so the Core TryAddSingleton default does not win.
        services.AddSingleton<IAdapterCallRecorder>(recorder);

        new WarpBuilder<TestContext>(services).AddAdapter("vendor", a =>
        {
            a.Recording.RecordCalls = CallRecording.FailuresOnly;
            a.Recording.EnrichCall = scope => scope.SetTag("enriched", "yes");
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => new RefitStubHandler()));
        });

        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("vendor");

        using var response = await client.GetAsync("https://api.vendor.com/orders", Xunit.TestContext.Current.CancellationToken);

        var record = recorder.Records.ShouldHaveSingleItem();

        // FailuresOnly gated the success into SuppressLog — proves the registered options reached the
        // registry rather than the unknown-name All default.
        record.SuppressLog.ShouldBeTrue();

        // The EnrichCall hook ran at completion.
        record.Tags.ShouldNotBeNull().ShouldContain(new KeyValuePair<string, string>("enriched", "yes"));
    }

    [TimedFact]
    public async Task AddAdapter_ObservabilityOnly_NoRetryOrRateLimit_OneAttempt_OneRecord()
    {
        // No user handlers / UseSharedRateLimit: the handler chain is just WarpAdapterHandler over the
        // primary handler. A counting primary handler proves it — exactly one physical attempt (no retry)
        // and exactly one recorded logical call (the outermost WarpAdapterHandler ran).
        var recorder = new CapturingRecorder();
        var stub = new CountingStubHandler();
        var provider = BuildProvider(recorder, "obs-only", a =>
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => stub)));

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("obs-only");
            using var response = await client.GetAsync("https://api.vendor.com/orders", Ct);

            stub.Invocations.ShouldBe(1);
            recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
        }
    }

    [TimedFact]
    public async Task AddAdapter_UserRetryHandler_RetriesInsideAdapterHandler_TwoAttempts_OneRecord()
    {
        // Resilience is a user-supplied handler added through ConfigureHttpClientBuilder (Warp takes no
        // retry dependency). It must nest INSIDE WarpAdapterHandler, so a retried 500→200 is two physical
        // attempts but one logical call: the counting handler sees 2, the recorder sees exactly 1.
        var recorder = new CapturingRecorder();
        var stub = new CountingStubHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var provider = BuildProvider(recorder, "resilient", a =>
        {
            a.ConfigureHttpClientBuilder(b => b.AddHttpMessageHandler(() => new RetryOnceHandler()));
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => stub));
        });

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilient");
            using var response = await client.GetAsync("https://api.vendor.com/orders", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            stub.Invocations.ShouldBe(2);

            var record = recorder.Records.ShouldHaveSingleItem();
            record.Outcome.ShouldBe(AdapterCallOutcome.Success);

            // Two physical attempts, ONE logical call: the WarpAdapterHandler sits outside the retry
            // handler and records Attempts == 1 (the documented logical-call fallback), not the retry count.
            record.Attempts.ShouldBe(1);
        }
    }

    [TimedFact]
    public async Task AddAdapter_UseSharedRateLimit_LeasesOneTokenPerPhysicalAttempt()
    {
        // The shared rate-limit handler is the innermost handler (one token per physical attempt). A fake
        // limiter keeps this NoDb while proving the handler is wired: the single attempt leased one token.
        var recorder = new CapturingRecorder();
        var limiter = new CountingRateLimiter();
        var stub = new CountingStubHandler();
        var provider = BuildProvider(
            recorder,
            "limited",
            a =>
            {
                a.UseSharedRateLimit(10, 60, AdapterRateLimitOverflow.FailFast);
                a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => stub));
            },
            services => services.AddSingleton<IAdapterRateLimiter>(limiter));

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("limited");
            using var response = await client.GetAsync("https://api.vendor.com/orders", Ct);

            limiter.Acquisitions.ShouldBe(1);
            limiter.LastAdapter.ShouldBe("limited");
            stub.Invocations.ShouldBe(1);
            recorder.Records.ShouldHaveSingleItem();
        }
    }

    [TimedFact]
    public async Task AddAdapter_UserRetryHandlerAndSharedRateLimit_LeasesOneTokenPerPhysicalAttempt()
    {
        // The documented chain is Warp handler → your handlers → rate limit → transport, so a retried call
        // must lease one token PER PHYSICAL ATTEMPT ("the vendor counts attempts, not logical calls"). A
        // registration reorder that hoisted the rate-limit handler outside the user handlers would lease
        // one token per logical call and silently over-admit against the shared budget on every retry.
        var recorder = new CapturingRecorder();
        var limiter = new CountingRateLimiter();
        var stub = new CountingStubHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var provider = BuildProvider(
            recorder,
            "resilient-limited",
            a =>
            {
                a.ConfigureHttpClientBuilder(b => b.AddHttpMessageHandler(() => new RetryOnceHandler()));
                a.UseSharedRateLimit(10, 60, AdapterRateLimitOverflow.FailFast);
                a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => stub));
            },
            services => services.AddSingleton<IAdapterRateLimiter>(limiter));

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("resilient-limited");
            using var response = await client.GetAsync("https://api.vendor.com/orders", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            stub.Invocations.ShouldBe(2);
            limiter.Acquisitions.ShouldBe(2);
            recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Success);
        }
    }

    [TimedFact]
    public async Task AddAdapter_RateLimiterRejects_RecordsThrottledAndNeverReachesTransport()
    {
        // A genuinely rejecting limiter, through the REAL handler chain: the AdapterRateLimitedException
        // must map to a Throttled outcome in the outermost WarpAdapterHandler's catch, rethrow to the
        // caller, and the transport must never be hit.
        var recorder = new CapturingRecorder();
        var stub = new CountingStubHandler();
        var provider = BuildProvider(
            recorder,
            "rejected",
            a =>
            {
                a.UseSharedRateLimit(1, 60, AdapterRateLimitOverflow.FailFast);
                a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => stub));
            },
            services => services.AddSingleton<IAdapterRateLimiter>(new RejectingRateLimiter()));

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("rejected");

            await Should.ThrowAsync<AdapterRateLimitedException>(async () =>
                await client.GetAsync("https://api.vendor.com/orders", Ct));

            stub.Invocations.ShouldBe(0);
            recorder.Records.ShouldHaveSingleItem().Outcome.ShouldBe(AdapterCallOutcome.Throttled);
        }
    }

    [TimedFact]
    public async Task AddAdapter_ConfigureHttpClientCalledTwice_ChainsBothDelegatesInOrder()
    {
        // ConfigureHttpClient composes repeated calls manually (existing(client); configure(client)) —
        // both must run, in registration order, so a later call can build on (or override) an earlier one.
        var recorder = new CapturingRecorder();
        var provider = BuildProvider(recorder, "chained", a =>
        {
            a.ConfigureHttpClient(c => c.DefaultRequestHeaders.Add("X-First", "1"));
            a.ConfigureHttpClient(c =>
                c.DefaultRequestHeaders.Add("X-Second", c.DefaultRequestHeaders.Contains("X-First") ? "after-first" : "first-missing"));
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => new RefitStubHandler()));
        });

        await using (provider)
        {
            using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("chained");

            client.DefaultRequestHeaders.GetValues("X-First").ShouldBe(["1"]);
            client.DefaultRequestHeaders.GetValues("X-Second").ShouldBe(["after-first"]);
        }
    }

    [TimedFact]
    public async Task AddAdapter_AddTypedClient_RecordsCallsThroughAdapterPipeline()
    {
        // The documented typed-client binding: a TClient resolved from DI must ride the same named-client
        // handler chain, so its calls are recorded like any named-client call.
        var recorder = new CapturingRecorder();
        var provider = BuildProvider(recorder, "typed", a =>
        {
            a.AddTypedClient<TypedVendorClient>();
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => new RefitStubHandler()));
        });

        await using (provider)
        {
            var typed = provider.GetRequiredService<TypedVendorClient>();
            await typed.PingAsync(Ct);

            recorder.Records.ShouldHaveSingleItem().AdapterName.ShouldBe("typed");
        }
    }

    [TimedFact]
    public void AddAdapter_NameContainsColon_Throws()
    {
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());

        Should.Throw<ArgumentException>(() => builder.AddAdapter("bad:name", _ => { }));
    }

    [TimedFact]
    public void AddAdapter_NameExceeds200Chars_Throws()
    {
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());

        Should.Throw<ArgumentException>(() => builder.AddAdapter(new string('x', 201), _ => { }));
    }

    [TimedFact]
    public void AddAdapter_DuplicateName_Throws()
    {
        // A second AddAdapter with the same name would double-wire the recording handler (named-options
        // handler actions accumulate) and double every recorded call — reject it up front.
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());
        builder.AddAdapter("vendor", _ => { });

        var ex = Should.Throw<InvalidOperationException>(() => builder.AddAdapter("vendor", _ => { }));
        ex.Message.ShouldContain("vendor");
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(
        CapturingRecorder recorder,
        string name,
        Action<WarpAdapterHttpOptions> configure,
        Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        // Register the capturing recorder first so the Core TryAddSingleton default does not win.
        services.AddSingleton<IAdapterCallRecorder>(recorder);
        extra?.Invoke(services);

        new WarpBuilder<TestContext>(services).AddAdapter(name, configure);

        return services.BuildServiceProvider();
    }
}

/// <summary>
/// Stand-in for whatever resilience package a consumer wires through
/// <c>ConfigureHttpClientBuilder</c>: retries a 5xx exactly once. Warp references no retry library, and
/// these tests assert Warp's own handler ORDERING contract (retry nests inside the observing handler and
/// outside the shared rate limiter), not a third-party retry implementation.
/// </summary>
internal sealed class RetryOnceHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode < 500)
        {
            return response;
        }

        response.Dispose();

        return await base.SendAsync(request, cancellationToken);
    }
}

/// <summary>
/// Primary <see cref="HttpMessageHandler"/> that counts physical attempts and returns a queued status
/// sequence (defaulting to <see cref="HttpStatusCode.OK"/> once exhausted) — proves retry / no-retry
/// behaviour without a live network.
/// </summary>
internal sealed class CountingStubHandler : HttpMessageHandler
{
    private readonly Queue<HttpStatusCode> _statuses;

    public CountingStubHandler(params HttpStatusCode[] statuses) => _statuses = new Queue<HttpStatusCode>(statuses);

    public int Invocations { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Invocations++;
        var status = _statuses.Count > 0 ? _statuses.Dequeue() : HttpStatusCode.OK;

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("\"ok\"") });
    }
}

/// <summary>Typed client bound to the adapter's named <see cref="HttpClient"/> via <c>AddTypedClient</c>.</summary>
internal sealed class TypedVendorClient
{
    private readonly HttpClient _client;

    public TypedVendorClient(HttpClient client) => _client = client;

    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await _client.GetAsync(new Uri("https://api.vendor.com/ping"), ct);
    }
}

/// <summary>Fake <see cref="IAdapterRateLimiter"/> that always rejects — proves the Throttled mapping e2e.</summary>
internal sealed class RejectingRateLimiter : IAdapterRateLimiter
{
    public Task AcquireAsync(string adapter, int limit, int perSeconds, AdapterRateLimitOverflow overflow, TimeSpan maxWait, CancellationToken ct)
        => throw new AdapterRateLimitedException($"Adapter '{adapter}' shared rate limit exceeded; failing fast.");
}

/// <summary>
/// Fake <see cref="IAdapterRateLimiter"/> that records each acquisition and always admits — lets the
/// rate-limit handler wiring be asserted without touching the DB (NoDb).
/// </summary>
internal sealed class CountingRateLimiter : IAdapterRateLimiter
{
    public int Acquisitions { get; private set; }

    public string? LastAdapter { get; private set; }

    public Task AcquireAsync(string adapter, int limit, int perSeconds, AdapterRateLimitOverflow overflow, TimeSpan maxWait, CancellationToken ct)
    {
        Acquisitions++;
        LastAdapter = adapter;

        return Task.CompletedTask;
    }
}

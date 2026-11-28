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
/// NoDb coverage for the Refit binding (SC8): a Refit-registered adapter records operation names equal
/// to the interface method names, driven through a stubbed <see cref="HttpMessageHandler"/> (no live
/// network). Also unit-tests the <c>RefitOperationNameReader</c> that bridges Refit's
/// <see cref="RestMethodInfo"/> to the Warp operation name.
/// </summary>
[Trait("Category", "NoDb")]
public class RefitAdapterTests
{
    [TimedFact]
    public async Task RefitGet_RecordsOperationAsMethodName()
    {
        var (provider, recorder) = BuildAdapter();
        var api = provider.GetRequiredService<IProbeApi>();

        await api.GetThing(42);

        var record = recorder.Records.ShouldHaveSingleItem();
        record.AdapterName.ShouldBe("probe");
        record.Operation.ShouldBe(nameof(IProbeApi.GetThing));
        record.Outcome.ShouldBe(AdapterCallOutcome.Success);
    }

    [TimedFact]
    public async Task RefitPost_RecordsOperationAsMethodName()
    {
        var (provider, recorder) = BuildAdapter();
        var api = provider.GetRequiredService<IProbeApi>();

        await api.CreateThing();

        recorder.Records.ShouldHaveSingleItem().Operation.ShouldBe(nameof(IProbeApi.CreateThing));
    }

    [TimedFact]
    public async Task MultipleCalls_RecordEachMethodNameInOrder()
    {
        var (provider, recorder) = BuildAdapter();
        var api = provider.GetRequiredService<IProbeApi>();

        await api.GetThing(1);
        await api.CreateThing();

        recorder.Records
            .Select(x => x.Operation)
            .ShouldBe([nameof(IProbeApi.GetThing), nameof(IProbeApi.CreateThing)]);
    }

    [TimedFact]
    public async Task RefitPostWithBody_RequestCaptureOn_SendsWholeBody_AndCapturesIt()
    {
        // Refit serializes [Body] arguments as a streamed JsonContent, which the adapter wraps rather than
        // buffers (#365): the transport still receives the whole body and the capture records it.
        var transport = new RefitBodyEchoHandler();
        var (provider, recorder) = BuildAdapter(transport, a => a.Recording.CaptureRequestBodies = CaptureMode.Always);
        var api = provider.GetRequiredService<IProbeApi>();

        await api.SendThing(new ProbeThing("widget", 3));

        transport.ReceivedBody.ShouldBe("{\"name\":\"widget\",\"count\":3}");
        var record = recorder.Records.ShouldHaveSingleItem();
        record.Operation.ShouldBe(nameof(IProbeApi.SendThing));
        record.RequestBody.ShouldBe(transport.ReceivedBody);
    }

    [TimedFact]
    public void AddAdapter_DuplicateNameThroughRefitEntryPoint_Throws()
    {
        // The Refit sugar wraps AddRefitClient THEN AddAdapter — the name/duplicate validation lives in
        // the latter and must still fire through this entry point.
        var builder = new WarpBuilder<TestContext>(new ServiceCollection());
        builder.AddAdapter<IProbeApi>("dup-probe", _ => { });

        var ex = Should.Throw<InvalidOperationException>(() => builder.AddAdapter<IProbeApi>("dup-probe", _ => { }));
        ex.Message.ShouldContain("dup-probe");
    }

    [TimedFact]
    public async Task RefitName_WinsOverUserAmbientScope_DocumentedPin()
    {
        // Pins a deliberate consequence of the Refit reader pushing its own ambient scope INSIDE any
        // user-opened one: for Refit-bound adapters the interface method name always beats a caller's
        // ambient WarpAdapterCall.Operation override. If this precedence is ever inverted on purpose,
        // update this pin and the package docs together.
        var (provider, recorder) = BuildAdapter();
        var api = provider.GetRequiredService<IProbeApi>();

        using (WarpAdapterCall.Operation("UserAmbientName"))
        {
            await api.GetThing(7);
        }

        recorder.Records.ShouldHaveSingleItem().Operation.ShouldBe(nameof(IProbeApi.GetThing));
    }

    [TimedFact]
    public void ReadOperationName_FromRestMethodInfoOption_ReturnsMethodName()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/things/1");
        var methodInfo = typeof(IProbeApi).GetMethod(nameof(IProbeApi.GetThing))!;
        var restMethodInfo = new RestMethodInfo(
            nameof(IProbeApi.GetThing),
            typeof(IProbeApi),
            methodInfo,
            "/things/{id}",
            typeof(Task<string>));
        request.Options.Set(new HttpRequestOptionsKey<RestMethodInfo>(HttpRequestMessageOptions.RestMethodInfo), restMethodInfo);

        RefitOperationNameReader.ReadOperationName(request).ShouldBe(nameof(IProbeApi.GetThing));
    }

    [TimedFact]
    public void ReadOperationName_NonRefitRequest_ReturnsNull()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/things/1");

        RefitOperationNameReader.ReadOperationName(request).ShouldBeNull();
    }

    private static (ServiceProvider Provider, CapturingRecorder Recorder) BuildAdapter()
        => BuildAdapter(new RefitStubHandler(), _ => { });

    private static (ServiceProvider Provider, CapturingRecorder Recorder) BuildAdapter(HttpMessageHandler primary, Action<WarpAdapterHttpOptions> configure)
    {
        // Reuse the real WarpAdapters over a capturing recorder so we can assert the recorded operation
        // name; register it as IWarpAdapters so the resolved WarpAdapterHandler drives it.
        var (adapters, recorder, _) = AdapterTestHarness.CreateAdapters(adapterName: "probe");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWarpAdapters>(adapters);

        var builder = new WarpBuilder<TestContext>(services);
        builder.AddAdapter<IProbeApi>("probe", a =>
        {
            a.BaseUrl = new Uri("https://api.probe.test");
            a.ConfigureHttpClientBuilder(b => b.ConfigurePrimaryHttpMessageHandler(() => primary));
            configure(a);
        });

        return (services.BuildServiceProvider(), recorder);
    }
}

/// <summary>A Refit interface used only by the Refit-binding tests; never calls a live endpoint.</summary>
public interface IProbeApi
{
    [Get("/things/{id}")]
    Task<string> GetThing(int id);

    [Post("/things")]
    Task<string> CreateThing();

    [Post("/things/body")]
    Task<string> SendThing([Body] ProbeThing thing);
}

/// <summary>A request body for the Refit capture test.</summary>
public sealed record ProbeThing(string Name, int Count);

/// <summary>Returns a fresh success response with a JSON string body for every request (no network).</summary>
internal sealed class RefitStubHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"ok\"") });
}

/// <summary>Reads the request body the way a transport does and keeps it, then answers like <see cref="RefitStubHandler"/>.</summary>
internal sealed class RefitBodyEchoHandler : HttpMessageHandler
{
    public string? ReceivedBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await using var sink = new MemoryStream();
        await request.Content!.CopyToAsync(sink, cancellationToken);
        ReceivedBody = System.Text.Encoding.UTF8.GetString(sink.ToArray());

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("\"ok\"") };
    }
}

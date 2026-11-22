using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class StreamEdgeCasesTests
{
    [TimedFact]
    public async Task EmptyStream_Returns200WithNoFrames()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/empty", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBeEmpty();
    }

    [TimedFact]
    public async Task SingleItemStream_EmitsOneFrame()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=1", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        body.ShouldBe("data: 0\n\n");
    }

    [TimedFact]
    public async Task LargeStream_EmitsAllFrames()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.GetAsync(new Uri("/api/stream/numbers?Count=200", UriKind.Relative));
        var body = await resp.Content.ReadAsStringAsync();

        // Each frame is "data: <n>\n\n" — count the frame separators.
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Length.ShouldBe(200);
    }

    [TimedFact]
    public async Task StreamThrowsMidWay_ResponseFailsWithTheHandlersException()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // The handler yields one frame and then throws. Headers are already sent, so the status is 200
        // and the failure can only show as the body being cut short.
        using var resp = await app.Client.GetAsync(
            new Uri("/api/stream/throws", UriKind.Relative),
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var body = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);

        // Reading to the end must FAIL with the handler's exception, never finish as if the stream had
        // simply ended. Whether the frame written before the throw can still be read is up to the
        // transport (the test host drops buffered bytes when the request aborts), so it is not asserted.
        var failure = await Should.ThrowAsync<Exception>(async () => await reader.ReadToEndAsync(ct));
        failure.ToString().ShouldContain("stream-failure");
    }
}

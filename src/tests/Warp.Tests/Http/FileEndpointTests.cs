using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// End-to-end coverage of the two file features over a real (TestServer) HTTP round-trip:
/// uploading a file and echoing its contents back, and sending text and receiving it back as a
/// downloadable file. The endpoints are generated from <c>[WarpHttpPost]</c> handlers — no
/// hand-written MapPost.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class FileEndpointTests
{
    [TimedFact]
    public async Task UploadTextFile_EchoesContentsBack()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        const string fileText = "the quick brown fox\njumped over\n";
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(fileText));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "File", "notes.txt");

        var resp = await app.Client.PostAsync(new Uri("/api/file-echo", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FileEchoResponse>();
        body.ShouldNotBeNull();
        body.FileName.ShouldBe("notes.txt");
        body.Length.ShouldBe(Encoding.UTF8.GetByteCount(fileText));
        body.Content.ShouldBe(fileText);
    }

    [TimedFact]
    public async Task UploadFileWithFormField_BindsBothFileAndField()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("payload")), "File", "data.bin" },
            { new StringContent("invoices"), "Tag" },
        };

        var resp = await app.Client.PostAsync(new Uri("/api/file-echo-tagged", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FileEchoTaggedResponse>();
        body.ShouldNotBeNull();
        body.Tag.ShouldBe("invoices");
        body.FileName.ShouldBe("data.bin");
        body.Length.ShouldBe(Encoding.UTF8.GetByteCount("payload"));
    }

    [TimedFact]
    public async Task SendText_ReturnsItAsDownloadableFile()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        const string text = "report line 1\nreport line 2\n";
        var resp = await app.Client.PostAsJsonAsync("/api/text-to-file", new { Text = text });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        resp.Content.Headers.ContentDisposition!.FileName.ShouldBe("echo.txt");

        // Verify the downloaded file's bytes round-trip the text we sent.
        var downloaded = await resp.Content.ReadAsByteArrayAsync();
        Encoding.UTF8.GetString(downloaded).ShouldBe(text);
    }

    [TimedFact]
    public async Task ResultWithStatus_ReturnsThatStatus()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var found = await app.Client.GetAsync(new Uri("/api/maybe-file/report", UriKind.Relative));
        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await found.Content.ReadAsStringAsync()).ShouldBe("found:report");

        var missing = await app.Client.GetAsync(new Uri("/api/maybe-file/missing", UriKind.Relative));
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsByteArrayAsync()).Length.ShouldBe(0);
    }

    [TimedFact]
    public async Task RouteAndFormFile_BindBothInOneRequest()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("contract")), "File", "contract.pdf" },
        };

        var resp = await app.Client.PostAsync(new Uri("/api/folders/42/files", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FolderUploadResponse>();
        body.ShouldNotBeNull();
        body.FolderId.ShouldBe(42);
        body.FileName.ShouldBe("contract.pdf");
    }

    [TimedFact]
    public async Task FormFileCollection_BindsAllUploadedFiles()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("a")), "Files", "a.txt" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("bb")), "Files", "b.txt" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("ccc")), "Files", "c.txt" },
        };

        var resp = await app.Client.PostAsync(new Uri("/api/file-multi", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UploadManyResponse>();
        body.ShouldNotBeNull();
        body.Count.ShouldBe(3);
        body.Names.ShouldBe(["a.txt", "b.txt", "c.txt"]);
    }

    [TimedFact]
    public async Task FileUpload_WithAntiforgeryMiddleware_StillSucceeds()
    {
        // Form endpoints get DisableAntiforgery() so programmatic uploads (no token) aren't
        // rejected even when the antiforgery middleware is in the pipeline.
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s => s.AddAntiforgery(),
            configureApp: a =>
            {
                a.UseAntiforgery();
                a.MapWarpHttp();
            });

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(Encoding.UTF8.GetBytes("x")), "File", "a.txt" },
        };

        var resp = await app.Client.PostAsync(new Uri("/api/file-echo", UriKind.Relative), content);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}

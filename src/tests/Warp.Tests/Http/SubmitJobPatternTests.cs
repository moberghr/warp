using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Testing;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// Verifies the doc-promoted pattern: an <c>IRequest&lt;Guid&gt;</c> wrapper handler
/// calls <c>IPublisher.Enqueue</c> and returns the resulting job ID. This is the
/// canonical way to submit background work via HTTP, since <c>IJob</c> / <c>IMessage</c>
/// are not directly HTTP-exposable.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class SubmitJobPatternTests
{
    [TimedFact]
    public async Task RequestWrapper_EnqueuesJobAndReturnsId()
    {
        var publisher = new InMemoryPublisher();

        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s => s.AddSingleton<IPublisher>(publisher),
            configureApp: a => a.MapWarpHttp());

        var resp = await app.Client.PostAsJsonAsync("/api/queue-work", new { Tag = "background-task" });

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var jobId = await resp.Content.ReadFromJsonAsync<Guid>();
        jobId.ShouldNotBe(Guid.Empty);

        var published = publisher.Published.ShouldHaveSingleItem();
        published.Kind.ShouldBe(PublishedJobKind.Job);
        var enqueued = published.Payload.ShouldBeOfType<EmptyJob>();
        enqueued.Tag.ShouldBe("background-task");
    }
}

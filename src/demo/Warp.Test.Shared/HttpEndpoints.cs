using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Http;

namespace Warp.Test.Shared;

// 1. IRequest<T> with a typed JSON response.
public sealed record HttpEchoRequest(string Text) : IRequest<HttpEchoResponse>;

public sealed record HttpEchoResponse(string Text, DateTime At);

[WarpHttpPost("/http/echo")]
public sealed class HttpEchoHandler : IRequestHandler<HttpEchoRequest, HttpEchoResponse>
{
    public Task<HttpEchoResponse> HandleAsync(HttpEchoRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpEchoResponse(request.Text, DateTime.UtcNow));
}

// 2. IRequest<Unit> → 204.
public sealed record DeleteNote([FromRoute] Guid Id) : IRequest<Unit>;

[WarpHttpDelete("/http/notes/{id}")]
public sealed class DeleteNoteHandler : IRequestHandler<DeleteNote, Unit>
{
    public Task<Unit> HandleAsync(DeleteNote request, CancellationToken cancellationToken) => Task.FromResult(Unit.Value);
}

// 3. IStreamRequest<T> → SSE.
public sealed record HttpNumberFeed([FromQuery] int Count) : IStreamRequest<int>;

[WarpHttpGet("/http/feed")]
public sealed class HttpNumberFeedHandler : IStreamRequestHandler<HttpNumberFeed, int>
{
    public async IAsyncEnumerable<int> HandleAsync(HttpNumberFeed request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.FromResult(i).ConfigureAwait(false);
        }
    }
}

// 4. "Submit a job via HTTP" — IRequest<Guid> wrapper that calls IPublisher.Enqueue.
public sealed record QueueEmailRequest(int EmailLogId) : IRequest<Guid>;

[WarpHttpPost("/http/queue-email")]
public sealed class QueueEmailHandler : IRequestHandler<QueueEmailRequest, Guid>
{
    private readonly IPublisher _publisher;

    public QueueEmailHandler(IPublisher publisher)
    {
        _publisher = publisher;
    }

    public async Task<Guid> HandleAsync(QueueEmailRequest request, CancellationToken cancellationToken)
    {
        var jobId = await _publisher.Enqueue(new SendEmailRequest { EmailLogId = request.EmailLogId }).ConfigureAwait(false);
        await _publisher.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return jobId;
    }
}

// 5. IHttpResponseShape — domain DTO customizes status + headers.
public sealed record HttpCreateOrder(string CustomerName) : IRequest<HttpCreatedOrder>;

public sealed record HttpCreatedOrder(Guid Id, string CustomerName) : IHttpResponseShape
{
    public void Apply(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = "/http/orders/" + Id;
    }
}

[WarpHttpPost("/http/orders")]
public sealed class HttpCreateOrderHandler : IRequestHandler<HttpCreateOrder, HttpCreatedOrder>
{
    public Task<HttpCreatedOrder> HandleAsync(HttpCreateOrder request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpCreatedOrder(Guid.NewGuid(), request.CustomerName));
}

// 6. Custom-policy authorization on a [WarpHttpPost] endpoint. Verifies §1.3 from the
// Arctic Adventures feedback: a custom IAuthorizationRequirement + AuthorizationHandler
// composes with [Authorize(Policy = "...")] on a Warp.Http endpoint exactly like on a
// raw MapPost. The handler in Warp.TestApp/Authentication/WebhookAuthorization.cs logs
// every invocation so an operator can confirm it fires for both grant and deny paths.
public sealed record WebhookEcho(string Payload) : IRequest<WebhookEchoResponse>;

public sealed record WebhookEchoResponse(string Payload, DateTime At);

[Authorize(Policy = "WebhookPassword")]
[WarpHttpPost("/http/webhook")]
public sealed class WebhookEchoHandler : IRequestHandler<WebhookEcho, WebhookEchoResponse>
{
    public Task<WebhookEchoResponse> HandleAsync(WebhookEcho request, CancellationToken cancellationToken)
        => Task.FromResult(new WebhookEchoResponse(request.Payload, DateTime.UtcNow));
}

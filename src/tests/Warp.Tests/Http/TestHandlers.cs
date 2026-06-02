using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Http;

namespace Warp.Tests.Http;

// Discovery-only types (Batch 1) — handlers tagged with [WarpHttp] but with minimal handler bodies.
public sealed record DiscoveryGetRecord(string Name) : IRequest<string>;

[WarpHttpGet("/discovery/get-record")]
public sealed class DiscoveryGetRecordHandler : IRequestHandler<DiscoveryGetRecord, string>
{
    public Task<string> HandleAsync(DiscoveryGetRecord request, CancellationToken cancellationToken) => Task.FromResult(request.Name);
}

public sealed class DiscoveryPostClass : IRequest<int>
{
    public int Value { get; set; }
}

[WarpHttpPost("/discovery/post-class", Group = "discovery")]
public sealed class DiscoveryPostClassHandler : IRequestHandler<DiscoveryPostClass, int>
{
    public Task<int> HandleAsync(DiscoveryPostClass request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
}

public sealed record DiscoveryStreamFeed : IStreamRequest<int>;

[WarpHttpGet("/discovery/stream-feed")]
public sealed class DiscoveryStreamFeedHandler : IStreamRequestHandler<DiscoveryStreamFeed, int>
{
    public async IAsyncEnumerable<int> HandleAsync(DiscoveryStreamFeed request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < 1; i++)
        {
            yield return await Task.FromResult(i).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}

// Real Batch 2 endpoints under /api/echo and /api/orders.
public sealed record EchoRequest(string Text) : IRequest<EchoResponse>;

public sealed record EchoResponse(string Text, DateTime At);

[WarpHttpPost("/api/echo")]
public sealed class EchoHandler : IRequestHandler<EchoRequest, EchoResponse>
{
    public Task<EchoResponse> HandleAsync(EchoRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EchoResponse(request.Text, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
}

public sealed record NoResponseRequest(string Tag) : IRequest<Unit>;

[WarpHttpPost("/api/no-response")]
public sealed class NoResponseHandler : IRequestHandler<NoResponseRequest, Unit>
{
    public Task<Unit> HandleAsync(NoResponseRequest request, CancellationToken cancellationToken) => Task.FromResult(Unit.Value);
}

public sealed record GetOrderById([FromRoute] Guid Id) : IRequest<OrderDto>;

public sealed record OrderDto(Guid Id, string Status);

[WarpHttpGet("/api/orders/{id}")]
public sealed class GetOrderByIdHandler : IRequestHandler<GetOrderById, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrderById request, CancellationToken cancellationToken)
        => Task.FromResult(new OrderDto(request.Id, "pending"));
}

public sealed record ListOrders([FromQuery] int Page, [FromQuery] int PageSize) : IRequest<ListOrdersResponse>;

public sealed record ListOrdersResponse(int Page, int PageSize);

[WarpHttpGet("/api/orders")]
public sealed class ListOrdersHandler : IRequestHandler<ListOrders, ListOrdersResponse>
{
    public Task<ListOrdersResponse> HandleAsync(ListOrders request, CancellationToken cancellationToken)
        => Task.FromResult(new ListOrdersResponse(request.Page, request.PageSize));
}

public sealed class SubmitOrder : IRequest<OrderDto>
{
    [FromRoute(Name = "tenantId")]
    public Guid TenantId { get; set; }

    [FromBody]
    public SubmitOrderBody Body { get; set; } = new(string.Empty);
}

public sealed record SubmitOrderBody(string Description);

[WarpHttpPost("/api/orders/{tenantId}/submit")]
public sealed class SubmitOrderHandler : IRequestHandler<SubmitOrder, OrderDto>
{
    public Task<OrderDto> HandleAsync(SubmitOrder request, CancellationToken cancellationToken)
        => Task.FromResult(new OrderDto(request.TenantId, request.Body.Description));
}

public sealed record WhoAmI([FromHeader(Name = "X-User-Id")] string UserId) : IRequest<string>;

[WarpHttpGet("/api/whoami")]
public sealed class WhoAmIHandler : IRequestHandler<WhoAmI, string>
{
    public Task<string> HandleAsync(WhoAmI request, CancellationToken cancellationToken) => Task.FromResult(request.UserId);
}

public sealed class UpdateProduct : IRequest<ProductDto>
{
    [FromRoute]
    public Guid Id { get; set; }

    [FromBody]
    public UpdateProductBody Body { get; set; } = new(string.Empty, 0);
}

public sealed record UpdateProductBody(string Name, decimal Price);

public sealed record ProductDto(Guid Id, string Name, decimal Price);

[WarpHttpPut("/api/products/{id}")]
public sealed class UpdateProductHandler : IRequestHandler<UpdateProduct, ProductDto>
{
    public Task<ProductDto> HandleAsync(UpdateProduct request, CancellationToken cancellationToken)
        => Task.FromResult(new ProductDto(request.Id, request.Body.Name, request.Body.Price));
}

public sealed record DeleteOrder([FromRoute] Guid Id) : IRequest<Unit>;

[WarpHttpDelete("/api/orders/{id}")]
public sealed class DeleteOrderHandler : IRequestHandler<DeleteOrder, Unit>
{
    public Task<Unit> HandleAsync(DeleteOrder request, CancellationToken cancellationToken) => Task.FromResult(Unit.Value);
}

public sealed class UpdateProductPrice : IRequest<ProductDto>
{
    [FromRoute]
    public Guid Id { get; set; }

    [FromBody]
    public UpdateProductPriceBody Body { get; set; } = new(0);
}

public sealed record UpdateProductPriceBody(decimal Price);

[WarpHttpPatch("/api/products/{id}/price")]
public sealed class UpdateProductPriceHandler : IRequestHandler<UpdateProductPrice, ProductDto>
{
    public Task<ProductDto> HandleAsync(UpdateProductPrice request, CancellationToken cancellationToken)
        => Task.FromResult(new ProductDto(request.Id, "patched", request.Body.Price));
}

// Init-only properties — class with init setters.
public sealed class InitOnlyRequest : IRequest<InitOnlyResponse>
{
    public string Name { get; init; } = string.Empty;

    public int Count { get; init; }
}

public sealed record InitOnlyResponse(string Name, int Count);

[WarpHttpPost("/api/init-only")]
public sealed class InitOnlyHandler : IRequestHandler<InitOnlyRequest, InitOnlyResponse>
{
    public Task<InitOnlyResponse> HandleAsync(InitOnlyRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new InitOnlyResponse(request.Name, request.Count));
}

// Response-shape — domain DTO customizes status + Location header.
public sealed record CreateOrderShaped(string CustomerName) : IRequest<CreatedOrderResponse>;

public sealed record CreatedOrderResponse(Guid Id, string CustomerName) : IHttpResponseShape
{
    public void Apply(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = "/api/orders/" + Id;
    }
}

[WarpHttpPost("/api/orders/created")]
public sealed class CreateOrderShapedHandler : IRequestHandler<CreateOrderShaped, CreatedOrderResponse>
{
    public Task<CreatedOrderResponse> HandleAsync(CreateOrderShaped request, CancellationToken cancellationToken)
        => Task.FromResult(new CreatedOrderResponse(new Guid("11111111-1111-1111-1111-111111111111"), request.CustomerName));
}

// Multi-attribute — same handler exposed at /v1 and /v2.
public sealed record MultiRouteRequest(string Tag) : IRequest<string>;

[WarpHttpPost("/api/v1/multi", Name = "MultiV1")]
[WarpHttpPost("/api/v2/multi", Name = "MultiV2")]
public sealed class MultiRouteRequestHandler : IRequestHandler<MultiRouteRequest, string>
{
    public Task<string> HandleAsync(MultiRouteRequest request, CancellationToken cancellationToken)
        => Task.FromResult("got: " + request.Tag);
}

// Group-tagged endpoints.
public sealed record GroupPublicPing : IRequest<string>;

[WarpHttpGet("/group-public/ping", Group = "public")]
public sealed class GroupPublicPingHandler : IRequestHandler<GroupPublicPing, string>
{
    public Task<string> HandleAsync(GroupPublicPing request, CancellationToken cancellationToken) => Task.FromResult("public-pong");
}

public sealed record GroupAdminPing : IRequest<string>;

[WarpHttpGet("/group-admin/ping", Group = "admin")]
public sealed class GroupAdminPingHandler : IRequestHandler<GroupAdminPing, string>
{
    public Task<string> HandleAsync(GroupAdminPing request, CancellationToken cancellationToken) => Task.FromResult("admin-pong");
}

// Auth metadata — [Authorize] / [AllowAnonymous] go on the handler now.
public sealed record SecureEcho : IRequest<string>;

[Authorize(Policy = "WarpHttpTestPolicy")]
[WarpHttpGet("/api/secure/echo")]
public sealed class SecureEchoHandler : IRequestHandler<SecureEcho, string>
{
    public Task<string> HandleAsync(SecureEcho request, CancellationToken cancellationToken) => Task.FromResult("secure");
}

public sealed record AnonEcho : IRequest<string>;

[AllowAnonymous]
[WarpHttpGet("/api/anon/echo")]
public sealed class AnonEchoHandler : IRequestHandler<AnonEcho, string>
{
    public Task<string> HandleAsync(AnonEcho request, CancellationToken cancellationToken) => Task.FromResult(request.GetType().Name);
}

// Custom-requirement policy — exercises IAuthorizationHandler<TRequirement> on a [WarpHttp*]
// endpoint. The requirement validates a custom header against an expected value; the
// authorization handler is the only thing that can grant access. Used by
// CustomAuthorizationRequirementTests to confirm the colleague's §1.3 scenario works.
public sealed record WebhookEcho : IRequest<string>;

[Authorize(Policy = "WebhookPasswordPolicy")]
[WarpHttpPost("/api/secure/webhook")]
public sealed class WebhookEchoHandler : IRequestHandler<WebhookEcho, string>
{
    public Task<string> HandleAsync(WebhookEcho request, CancellationToken cancellationToken) => Task.FromResult("authorized");
}

// Rate-limit metadata — [EnableRateLimiting] on the handler must surface as endpoint
// metadata so ASP.NET's rate-limiting middleware applies the named policy.
public sealed record RateLimitedEcho : IRequest<string>;

[EnableRateLimiting("WarpHttpTestRateLimit")]
[WarpHttpGet("/api/rate-limited/echo")]
public sealed class RateLimitedEchoHandler : IRequestHandler<RateLimitedEcho, string>
{
    public Task<string> HandleAsync(RateLimitedEcho request, CancellationToken cancellationToken) => Task.FromResult("rate-limited");
}

// Stream endpoints with array binding via Minimal API.
public sealed record NumbersStream([FromQuery] int Count) : IStreamRequest<int>;

[WarpHttpGet("/api/stream/numbers")]
public sealed class NumbersStreamHandler : IStreamRequestHandler<NumbersStream, int>
{
    public async IAsyncEnumerable<int> HandleAsync(NumbersStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.FromResult(i).ConfigureAwait(false);
        }
    }
}

public sealed record EmptyStream : IStreamRequest<string>;

[WarpHttpGet("/api/stream/empty")]
public sealed class EmptyStreamHandler : IStreamRequestHandler<EmptyStream, string>
{
#pragma warning disable CS1998
    public async IAsyncEnumerable<string> HandleAsync(EmptyStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
#pragma warning restore CS1998
    {
        yield break;
    }
}

public sealed record ThrowingStream : IStreamRequest<string>;

[WarpHttpGet("/api/stream/throws")]
public sealed class ThrowingStreamHandler : IStreamRequestHandler<ThrowingStream, string>
{
    public async IAsyncEnumerable<string> HandleAsync(ThrowingStream request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return await Task.FromResult("first").ConfigureAwait(false);
        throw new InvalidOperationException("stream-failure");
    }
}

// Failure / cancellation paths used by HandlerErrorTests.
public sealed record ThrowingRequest(string Marker) : IRequest<string>;

[WarpHttpPost("/api/throws")]
public sealed class ThrowingHandler : IRequestHandler<ThrowingRequest, string>
{
    public Task<string> HandleAsync(ThrowingRequest request, CancellationToken cancellationToken)
        => throw new InvalidOperationException("boom: " + request.Marker);
}

public sealed record CancellingRequest(string Marker) : IRequest<string>;

[WarpHttpPost("/api/cancels")]
public sealed class CancellingHandler : IRequestHandler<CancellingRequest, string>
{
    public Task<string> HandleAsync(CancellingRequest request, CancellationToken cancellationToken)
        => throw new OperationCanceledException(cancellationToken);
}

// === Model-binding tests — verify our generator's [AsParameters] / Mixed wiring hands ===
// === off correctly to ASP.NET. Covers bool, array, int, Guid, nullable, header, etc. ===

// Bool — single property in AsParameters mode.
public sealed record BindingBoolQuery([FromQuery] bool Active) : IRequest<BindingBoolResponse>;

public sealed record BindingBoolResponse(bool Active);

[WarpHttpGet("/api/binding/bool")]
public sealed class BindingBoolHandler : IRequestHandler<BindingBoolQuery, BindingBoolResponse>
{
    public Task<BindingBoolResponse> HandleAsync(BindingBoolQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingBoolResponse(request.Active));
}

// String array — repeated query keys → string[].
public sealed record BindingStringArrayQuery([FromQuery] string[] Tags) : IRequest<BindingStringArrayResponse>;

public sealed record BindingStringArrayResponse(string[] Tags);

[WarpHttpGet("/api/binding/strings")]
public sealed class BindingStringArrayHandler : IRequestHandler<BindingStringArrayQuery, BindingStringArrayResponse>
{
    public Task<BindingStringArrayResponse> HandleAsync(BindingStringArrayQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingStringArrayResponse(request.Tags));
}

// Int array — repeated query keys parsed as int[].
public sealed record BindingIntArrayQuery([FromQuery] int[] Ids) : IRequest<BindingIntArrayResponse>;

public sealed record BindingIntArrayResponse(int[] Ids);

[WarpHttpGet("/api/binding/ints")]
public sealed class BindingIntArrayHandler : IRequestHandler<BindingIntArrayQuery, BindingIntArrayResponse>
{
    public Task<BindingIntArrayResponse> HandleAsync(BindingIntArrayQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingIntArrayResponse(request.Ids));
}

// Nullable int — distinguishes "missing" from "0".
public sealed record BindingNullableIntQuery([FromQuery] int? Page) : IRequest<BindingNullableIntResponse>;

public sealed record BindingNullableIntResponse(bool HasValue, int Value);

[WarpHttpGet("/api/binding/nullable-int")]
public sealed class BindingNullableIntHandler : IRequestHandler<BindingNullableIntQuery, BindingNullableIntResponse>
{
    public Task<BindingNullableIntResponse> HandleAsync(BindingNullableIntQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingNullableIntResponse(request.Page.HasValue, request.Page ?? -1));
}

// Mixed sources — one [FromRoute], one [FromQuery], one [FromHeader].
public sealed record BindingMixedSources(
    [FromRoute] Guid Id,
    [FromQuery] string Name,
    [FromHeader(Name = "X-Trace-Id")] string TraceId) : IRequest<BindingMixedSourcesResponse>;

public sealed record BindingMixedSourcesResponse(Guid Id, string Name, string TraceId);

[WarpHttpGet("/api/binding/mixed/{id}")]
public sealed class BindingMixedSourcesHandler : IRequestHandler<BindingMixedSources, BindingMixedSourcesResponse>
{
    public Task<BindingMixedSourcesResponse> HandleAsync(BindingMixedSources request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingMixedSourcesResponse(request.Id, request.Name, request.TraceId));
}

// Route token name match without explicit [FromRoute] — ASP.NET infers FromRoute when the
// property name matches a route token.
public sealed record BindingRouteByName(Guid Id) : IRequest<BindingRouteByNameResponse>;

public sealed record BindingRouteByNameResponse(Guid Id);

[WarpHttpGet("/api/binding/route-by-name/{id}")]
public sealed class BindingRouteByNameHandler : IRequestHandler<BindingRouteByName, BindingRouteByNameResponse>
{
    public Task<BindingRouteByNameResponse> HandleAsync(BindingRouteByName request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingRouteByNameResponse(request.Id));
}

// Route template constraint — `:guid` rejects non-GUIDs at routing time.
public sealed record BindingGuidConstraint([FromRoute] Guid Id) : IRequest<BindingGuidConstraintResponse>;

public sealed record BindingGuidConstraintResponse(Guid Id);

[WarpHttpGet("/api/binding/constraint-guid/{id:guid}")]
public sealed class BindingGuidConstraintHandler : IRequestHandler<BindingGuidConstraint, BindingGuidConstraintResponse>
{
    public Task<BindingGuidConstraintResponse> HandleAsync(BindingGuidConstraint request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingGuidConstraintResponse(request.Id));
}

// Int route constraint — `:int` rejects non-int route values.
public sealed record BindingIntConstraint([FromRoute] int Year) : IRequest<BindingIntConstraintResponse>;

public sealed record BindingIntConstraintResponse(int Year);

[WarpHttpGet("/api/binding/constraint-int/{year:int}")]
public sealed class BindingIntConstraintHandler : IRequestHandler<BindingIntConstraint, BindingIntConstraintResponse>
{
    public Task<BindingIntConstraintResponse> HandleAsync(BindingIntConstraint request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingIntConstraintResponse(request.Year));
}

// Body-only with [FromBody] explicit — exercises Mixed shape with no other source attrs.
public sealed class BodyOnlyClass : IRequest<BodyOnlyResponse>
{
    [FromBody]
    public BodyOnlyDto Body { get; set; } = new(string.Empty);
}

public sealed record BodyOnlyDto(string Tag);

public sealed record BodyOnlyResponse(string Tag);

[WarpHttpPost("/api/binding/body-only")]
public sealed class BodyOnlyHandler : IRequestHandler<BodyOnlyClass, BodyOnlyResponse>
{
    public Task<BodyOnlyResponse> HandleAsync(BodyOnlyClass request, CancellationToken cancellationToken)
        => Task.FromResult(new BodyOnlyResponse(request.Body.Tag));
}

// Class with init-only properties + AsParameters (covers init-setter property binding).
public sealed class BindingInitOnlyQuery : IRequest<BindingInitOnlyResponse>
{
    [FromQuery]
    public string Name { get; init; } = string.Empty;

    [FromQuery]
    public int Count { get; init; }
}

public sealed record BindingInitOnlyResponse(string Name, int Count);

[WarpHttpGet("/api/binding/init-only-query")]
public sealed class BindingInitOnlyHandler : IRequestHandler<BindingInitOnlyQuery, BindingInitOnlyResponse>
{
    public Task<BindingInitOnlyResponse> HandleAsync(BindingInitOnlyQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new BindingInitOnlyResponse(request.Name, request.Count));
}

// Mixed shape with a single body target — POST with [FromRoute] route param + one bare
// scalar body param. Regression coverage for #208: this is the working Mixed path and
// must not trip WHTTP004.
public sealed record PromoteUser([FromRoute(Name = "id")] Guid Id, string NewRole) : IRequest<PromoteUserResponse>;

public sealed record PromoteUserResponse(Guid Id, string NewRole);

[WarpHttpPost("/api/users/{id}/promote")]
public sealed class PromoteUserHandler : IRequestHandler<PromoteUser, PromoteUserResponse>
{
    public Task<PromoteUserResponse> HandleAsync(PromoteUser request, CancellationToken cancellationToken)
        => Task.FromResult(new PromoteUserResponse(request.Id, request.NewRole));
}

// Submit-a-job pattern (#4) — IRequest<Guid> wrapper. We use a fake IPublisher in tests
// so this stays NoDb; real DB path is exercised end-to-end in the demo app.
public sealed record QueueWorkRequest(string Tag) : IRequest<Guid>;

[WarpHttpPost("/api/queue-work")]
public sealed class QueueWorkHandler(IPublisher publisher) : IRequestHandler<QueueWorkRequest, Guid>
{
    public async Task<Guid> HandleAsync(QueueWorkRequest request, CancellationToken cancellationToken)
    {
        var jobId = await publisher.Enqueue(new EmptyJob(request.Tag)).ConfigureAwait(false);
        return jobId;
    }
}

public sealed record EmptyJob(string Tag) : IJob;

public sealed class EmptyJobHandler : IJobHandler<EmptyJob>
{
    public Task HandleAsync(EmptyJob message, CancellationToken cancellationToken) => Task.CompletedTask;
}

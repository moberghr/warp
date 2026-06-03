---
sidebar_position: 11
---

# HTTP Endpoints (Warp.Http)

`Moberg.Warp.Http` is an optional package that exposes Warp `IRequest<TResponse>` and `IStreamRequest<TResponse>` handlers as ASP.NET Minimal API endpoints — annotate the **handler class**, run `MapWarpHttp()`, you have an HTTP endpoint. Source-generated dispatch (no per-request reflection); independent of `Warp.UI`.

`IJob` and `IMessage` cannot be HTTP-exposed — background-work types have async fire-and-forget semantics that don't fit synchronous request/response. The pattern for "submit a job via HTTP" is a thin `IRequest<Guid>` wrapper that calls `IPublisher.Enqueue` (see [§ Submit a job via HTTP](#submit-a-job-via-http)).

## Install

```xml
<PackageReference Include="Moberg.Warp.Http" Version="..." />
```

The NuGet ships the runtime library plus the source generator that discovers `[WarpHttp...]`-tagged handler classes in your assembly.

## Quick start

```csharp
using Microsoft.AspNetCore.Mvc;          // [FromRoute], [FromQuery], [FromHeader], [FromBody]
using Warp.Core.Handlers;
using Warp.Http;

// Define the request type — this is the public contract.
public sealed record GetOrder([FromRoute] Guid Id) : IRequest<OrderDto>;

// Tag the handler class with the HTTP method + route.
[WarpHttpGet("/orders/{id}")]
public sealed class GetOrderHandler : IRequestHandler<GetOrder, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrder request, CancellationToken ct)
        => Task.FromResult(new OrderDto(request.Id, "pending"));
}
```

Wire up at startup:

```csharp
builder.Services.AddWarpHttp();      // registers options + writers
// ... your other Warp registrations ...
app.MapWarpHttp();                    // discovers and maps tagged handlers
```

That's it. `GET /orders/<guid>` is now live and returns 200 + `OrderDto` JSON.

## How it works

The source generator finds every class tagged with `[WarpHttp...]` that implements `IRequestHandler<TReq, TRes>` or `IStreamRequestHandler<TReq, TRes>`, and emits a strongly-typed delegate per attribute. ASP.NET Minimal API parses route values, query strings, headers, and body using its full binding pipeline — including `IParsable<T>`, `TryParse`, query-string arrays, and content negotiation.

After binding, the generated delegate dispatches via `IMediator.Send` (or `CreateStream`), so the existing `IPipelineBehavior` chain runs unchanged: auth, validation, logging, anything you've registered for in-memory `Send` calls also runs on the HTTP path.

```
HTTP request
  ↓ ASP.NET Minimal API binding (route, query, header, body)
  ↓ Generated lambda — constructs TRequest, calls IMediator.Send
  ↓ IPipelineBehavior chain (your auth / validation / logging)
  ↓ Your IRequestHandler.HandleAsync
  ↓ JSON / SSE response
```

## Response semantics

| Handler kind                      | Status | Body                                       |
|-----------------------------------|--------|--------------------------------------------|
| `IRequest<TResponse>` non-Unit    | 200    | JSON of `TResponse`                        |
| `IRequest<Unit>`                  | 204    | empty                                      |
| `IStreamRequest<TResponse>`       | 200    | `text/event-stream` (one `data:` per item) |

A response type can implement `IHttpResponseShape` to override the default status / set headers / set Location:

```csharp
public sealed record CreatedOrder(Guid Id, string CustomerName) : IHttpResponseShape
{
    public void Apply(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.Response.Headers.Location = "/orders/" + Id;
    }
}
```

`Apply` runs after the handler returns and before the JSON body is serialized. Only fires for `IRequest<TResponse>` non-Unit responses — not for `IRequest<Unit>` (no body) or streams (status fixed for the stream's lifetime).

## Binding

Warp.Http delegates all parameter binding to ASP.NET Minimal API. Use the standard attributes from `Microsoft.AspNetCore.Mvc`:

| Source                                          | Attribute                       |
|-------------------------------------------------|---------------------------------|
| Route token (e.g. `{id}`)                        | `[FromRoute]`                   |
| Query string                                     | `[FromQuery]`                   |
| Request header                                   | `[FromHeader(Name = "X-Foo")]`  |
| JSON body                                        | `[FromBody]`                    |
| (default for body verbs without other attrs)     | the request itself becomes the body |
| (default for non-body verbs without other attrs) | route-token-name match → `[FromRoute]`, otherwise `[FromQuery]` |

### Binding shapes

The generator picks one of three lambda shapes depending on what your request type looks like:

**1. Whole-body POST** — request type with no per-property attributes on a body verb:

```csharp
public sealed record CreateOrder(string CustomerName, List<LineItem> Items) : IRequest<OrderDto>;

[WarpHttpPost("/orders")]
public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, OrderDto> { ... }
```

ASP.NET deserializes the entire JSON body into `CreateOrder`. The natural shape for plain POST DTOs.

**2. Decomposed (`[AsParameters]`)** — request type whose properties carry only `[FromRoute]` / `[FromQuery]` / `[FromHeader]`:

```csharp
public sealed record ListOrders(
    [FromQuery] int Page,
    [FromQuery] int PageSize,
    [FromHeader(Name = "X-Tenant-Id")] Guid TenantId) : IRequest<ListOrdersResponse>;

[WarpHttpGet("/orders")]
public sealed class ListOrdersHandler : IRequestHandler<ListOrders, ListOrdersResponse> { ... }
```

ASP.NET binds each property from its declared source via `[AsParameters]`. Records with primary constructors and classes with property setters both work.

> ⚠️ **A non-nullable scalar query param is REQUIRED — your C# default is ignored.** Under `[AsParameters]`, ASP.NET treats every non-nullable value-typed property as a required argument. A property initializer or parameter default (`int Take { get; set; } = 20;`) is **not** consulted by the binder, so a request that omits the parameter returns **400** rather than falling back to the default. This is the single most common Warp.Http surprise.
>
> ```csharp
> // ✘ Bare GET /orders → 400: Page is a required query param (the "= 1" is ignored).
> public sealed class ListOrders : IRequest<ListOrdersResponse>
> {
>     public int Page { get; set; } = 1;
> }
>
> // ✓ Make optional params nullable; apply the default in the handler.
> public sealed class ListOrders : IRequest<ListOrdersResponse>
> {
>     public int? Page { get; set; }   // omitted → null → handler applies the default
> }
> ```
>
> The generator emits a **`WHTTP005`** warning for any non-nullable value-typed query parameter that carries a C# default, so this is caught at build time. Reference-typed params (e.g. `string?`) are already optional and don't trip it. A param you genuinely want required can stay non-nullable with no default — `WHTTP005` only fires when a default is present (and silently dropped).

**3. Mixed body + route/query/header** — class with a `[FromBody]` property *and* other source attributes:

```csharp
public sealed class SubmitOrder : IRequest<OrderDto>
{
    [FromRoute(Name = "tenantId")]
    public Guid TenantId { get; set; }

    [FromBody]
    public SubmitOrderBody Body { get; set; } = new(string.Empty);
}

public sealed record SubmitOrderBody(string Description);

[WarpHttpPost("/orders/{tenantId}/submit")]
public sealed class SubmitOrderHandler : IRequestHandler<SubmitOrder, OrderDto> { ... }
```

The generator emits explicit lambda parameters per source and constructs `SubmitOrder` from the bound parts. ASP.NET's `[AsParameters]` doesn't support `[FromBody]` properties directly, so the generator handles this case explicitly.

> **One body parameter only.** ASP.NET Minimal API accepts at most one body-bound parameter per endpoint. On a body verb (POST / PUT / PATCH), any parameter that isn't annotated with `[FromRoute]` / `[FromQuery]` / `[FromHeader]` defaults to the body. If more than one parameter ends up body-bound (e.g. `[FromRoute] int Id` plus two bare scalars), the generator emits a `WHTTP004` error. Fix it by wrapping the body fields in a single record and tagging it `[FromBody]`:
>
> ```csharp
> // ✘ WHTTP004: Name and Price both default to the body
> public sealed record CreateOrder([FromRoute] int TenantId, string Name, decimal Price) : IRequest<OrderDto>;
>
> // ✓ one [FromBody] sub-record
> public sealed record CreateOrderBody(string Name, decimal Price);
> public sealed record CreateOrder([FromRoute] int TenantId, [FromBody] CreateOrderBody Body) : IRequest<OrderDto>;
> ```
>
> If no parameter is annotated at all, the verb defaults to **whole-body** binding (shape 1 above) and `TRequest` deserializes from the JSON body — that path has no multi-body limitation.

> **Tip:** for mixed binding, prefer **classes with property setters** over records with primary constructors. Attributes on record positional parameters apply to the parameter, not the synthesized property, which can confuse `[AsParameters]`. Classes with `{ get; set; }` or `{ get; init; }` properties are unambiguous.

### Records vs classes

Both work. Pick whatever matches your codebase style:

- **Record with primary ctor:** `record GetOrder([FromRoute] Guid Id) : IRequest<OrderDto>;`
- **Class with init-only properties:** `class GetOrder : IRequest<OrderDto> { [FromRoute] public Guid Id { get; init; } }`
- **Class with full setters:** `class GetOrder : IRequest<OrderDto> { [FromRoute] public Guid Id { get; set; } }`

For the mixed-binding shape, classes are recommended (see tip above).

### Verbs

```csharp
[WarpHttpGet("/path")]    // GET
[WarpHttpPost("/path")]   // POST
[WarpHttpPut("/path")]    // PUT
[WarpHttpPatch("/path")]  // PATCH
[WarpHttpDelete("/path")] // DELETE
```

GET and DELETE never read the request body — only POST/PUT/PATCH bind whole-body or mixed-body requests.

### Route template constraints

Route constraints work because they're pure ASP.NET — Warp.Http passes the template through:

```csharp
[WarpHttpGet("/orders/{id:guid}")]      // matches only valid GUIDs; non-GUID returns 404
[WarpHttpGet("/users/{age:int:min(0)}")] // matches non-negative ints
```

### Binding errors

Malformed JSON, unparseable values, missing required route tokens — all surface as ASP.NET's standard `BadHttpRequestException` (400) or whatever your registered exception middleware translates them to. Warp.Http does not impose its own error envelope; if your app has `AddProblemDetails()` and `UseExceptionHandler()`, those produce the response.

## Submit a job via HTTP

`IJob` and `IMessage` are explicitly **not** HTTP-exposable — tagging a handler whose request type implements either of them is a compile-time error (`WHTTP001`). The reason: synchronous HTTP request/response and asynchronous fire-and-forget background work have different success criteria. A `POST /jobs/...` endpoint that returns 202 might or might not surface a job ID, might or might not block, might or might not retry — every choice is wrong for someone.

The pattern is to write a thin wrapper `IRequest<Guid>` whose handler enqueues the real job:

```csharp
public sealed record EnqueueReport(Guid TenantId) : IRequest<Guid>;

[WarpHttpPost("/reports/generate")]
public sealed class EnqueueReportHandler(IPublisher publisher)
    : IRequestHandler<EnqueueReport, Guid>
{
    public async Task<Guid> HandleAsync(EnqueueReport req, CancellationToken ct)
    {
        var jobId = await publisher.Enqueue(new GenerateReportJob(req.TenantId));
        await publisher.SaveChangesAsync(ct);
        return jobId;
    }
}
```

The HTTP response is `200 + "<job-guid>"`. The actual work runs in your worker pool. The wrapper is explicit about its semantics — no framework magic.

## Streaming (SSE)

`IStreamRequest<TResponse>` becomes a `text/event-stream` endpoint. Each yielded item becomes a `data: <json>\n\n` frame:

```csharp
public sealed record OrderEventFeed([FromQuery] Guid TenantId) : IStreamRequest<OrderEvent>;

[WarpHttpGet("/orders/feed")]
public sealed class OrderEventFeedHandler : IStreamRequestHandler<OrderEventFeed, OrderEvent>
{
    public async IAsyncEnumerable<OrderEvent> HandleAsync(
        OrderEventFeed request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _events.Subscribe(request.TenantId, ct))
        {
            yield return evt;
        }
    }
}
```

`HttpContext.RequestAborted` is plumbed through to the handler — when the client disconnects, the `IAsyncEnumerable` enumerator's cancellation token fires.

## Versioning aliases (multi-attribute)

A handler class may carry multiple `[WarpHttp...]` attributes. Each must specify a unique `Name`:

```csharp
[WarpHttpPost("/v1/orders", Name = "CreateOrderV1")]
[WarpHttpPost("/v2/orders", Name = "CreateOrderV2")]
public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, OrderDto> { ... }
```

ASP.NET requires unique route names per endpoint; missing `Name` on any of multiple attributes is a compile-time error (`WHTTP002`).

## Named groups

Group attributes select a subset of endpoints to register on a particular `IEndpointRouteBuilder`. Useful for exposing a handler under a sub-path or applying group-level middleware:

```csharp
[WarpHttpPost("/orders", Group = "public")]
public sealed class CreateOrderPublicHandler : IRequestHandler<CreateOrder, OrderDto> { ... }

[WarpHttpPost("/admin/users", Group = "admin")]
public sealed class CreateAdminUserHandler : IRequestHandler<CreateAdminUser, Guid> { ... }
```

```csharp
app.MapWarpHttp();                                       // registers null-group handlers only
app.MapGroup("/api/public").RequireAuthorization("publicPolicy").MapWarpHttp("public");
app.MapGroup("/internal/admin").RequireAuthorization("adminPolicy").MapWarpHttp("admin");
```

`MapWarpHttp(group)` matches strictly — null matches null, "public" matches "public". No overlap. Calling `MapWarpHttp(group)` twice on the same `IEndpointRouteBuilder` instance with the same group throws `InvalidOperationException` at startup.

## Auth

Place `[Authorize]` or `[AllowAnonymous]` on the handler class — Warp.Http surfaces them as ASP.NET endpoint metadata, so they compose with group-level `RequireAuthorization()` exactly as Minimal API does:

```csharp
[Authorize(Policy = "OrdersWrite")]
[WarpHttpPost("/orders")]
public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, OrderDto> { ... }
```

```csharp
app.MapGroup("/api").RequireAuthorization().MapWarpHttp();
// Per-handler [AllowAnonymous] overrides the group's RequireAuthorization() on a single endpoint:

[AllowAnonymous]
[WarpHttpGet("/api/health")]
public sealed class HealthCheckHandler : IRequestHandler<HealthCheck, HealthStatus> { ... }
```

## OpenAPI / Swagger

Each registered endpoint emits standard ASP.NET endpoint metadata:

- `.Accepts<TRequest>("application/json")` for body verbs
- `.Produces<TResponse>(200)` (or `.Produces(204)` for `IRequest<Unit>`)
- `.WithName(name)` from the attribute
- `.WithTags(...)` derived from the route's first segment (e.g. `/api/orders/{id}` → `api`)

Swashbuckle, NSwag, and `Microsoft.AspNetCore.OpenApi` discover these automatically. No additional configuration needed.

## Pipeline behaviors

The HTTP path runs `IPipelineBehavior<TRequest, TResponse>` — the same chain that runs for in-memory `IMediator.Send`. Auth, validation, logging, anything you've registered as a pipeline behavior runs on HTTP requests for free:

```csharp
public sealed class LoggingBehavior<TRequest, TResponse>(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TRequest, TResponse> next, CancellationToken ct)
    {
        logger.LogInformation("Handling {RequestType}", typeof(TRequest).Name);
        var response = await next(request, ct);
        logger.LogInformation("Handled {RequestType}", typeof(TRequest).Name);
        return response;
    }
}
```

This logs every `IRequest<T>` whether dispatched in-memory or via HTTP.

`RetryPipelineBehavior` (constraint `where TRequest : IJob`) and `ConcurrencyPipelineBehavior` (`is not IJob` runtime check) self-scope to the worker path and never run on HTTP — appropriately, since blocking the HTTP request thread for retry delays would be a bug.

## Diagnostics

| ID         | Severity | Condition |
|------------|----------|-----------|
| `WHTTP001` | Error    | Handler class tagged with `[WarpHttp...]` either doesn't implement `IRequestHandler<,>` / `IStreamRequestHandler<,>`, or its request type implements `IJob` / `IMessage` (background-work types cannot be HTTP-exposed). |
| `WHTTP002` | Error    | Handler class has multiple `[WarpHttp...]` attributes but at least one is missing `Name = "..."`. ASP.NET requires unique route names per endpoint. |
| `WHTTP004` | Error    | Body verb (POST / PUT / PATCH) handler has more than one body-bound parameter. Minimal API accepts at most one — wrap the body fields in a single `[FromBody]` sub-record. |
| `WHTTP005` | Warning  | A non-nullable value-typed query parameter on a GET / DELETE handler carries a C# default. `[AsParameters]` binding ignores the default and makes the parameter required, so omitting it returns 400. Make it nullable and apply the default in the handler. |

## Independence from Warp.UI

`Moberg.Warp.Http` is structurally independent of `Moberg.Warp.UI`. The dashboard ships its own endpoints under `/warp` and is unrelated to this feature. You can use Warp.Http without Warp.UI, and vice versa.

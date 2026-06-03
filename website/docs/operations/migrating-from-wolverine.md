---
sidebar_position: 4
---

# Migrating from Wolverine

Wolverine and Warp solve overlapping problems — durable messaging, background jobs, in-memory dispatch, HTTP handlers — but the shape of the API is different in concrete places. This page is a translation table you can scan while porting code, plus notes on the patterns that don't have a 1:1 equivalent.

> If you're considering Warp as a Wolverine replacement, also read [Multi-Project Source Generation](./multi-project-source-gen.md) and [EF Core Integration](./ef-core-integration.md) — those two pages cover the lion's share of friction reports from teams who've done this migration.

## The cheat sheet

| Wolverine                                                      | Warp                                                                                  |
|----------------------------------------------------------------|---------------------------------------------------------------------------------------|
| `IMessageBus.InvokeAsync<TResponse>(request)`                  | `IMediator.Send(request)` — in-memory, returns `TResponse`                            |
| `IMessageBus.PublishAsync(message)`                            | `IPublisher.Publish(message)` + `IPublisher.SaveChangesAsync()`                       |
| `IMessageBus.PublishAsync(message, options)` (delayed)         | `IPublisher.Schedule(message, scheduleTime)` + `SaveChangesAsync()`                   |
| Cascading return: `Task<MyEvent> Handle(...)` → auto-publishes | Explicit: `publisher.Publish(new MyEvent(...))` inside the handler                    |
| `[WolverineHandler]` discovery                                 | None — class implementing `IJobHandler<>` / `IMessageHandler<>` / `IRequestHandler<,>` is discovered by source generation |
| Static handler methods (`public static void Handle(...)`)      | Instance class implementing the handler interface (static not supported)              |
| `[Authorize(Policy = "X")]` on a static `[WolverinePost]`      | `[Authorize(Policy = "X")]` on the handler class — same ASP.NET semantics             |
| `OnException<TException>().Requeue()` / `.MoveToErrorQueue()`  | An `IPipelineBehavior<,>` that catches and decides — or `[Retry(...)]` + throw        |
| `Wolverine.Tracking.InvokeMessageAndWaitAsync`                 | `IJobQueryService` polled by `TraceId` until terminal — no built-in helper yet        |
| Local queues `LocalQueueFor<T>(...)`                           | Worker group: `opt.Queues = ["my-queue"]`, dispatch by `JobParameters { Queue = "my-queue" }` |
| `IRetryPolicy` per handler                                     | `[Retry(maxAttempts: N, Delays = [...])]` attribute on the handler class              |
| Outbox: enabled by transport config                            | Outbox: always on — jobs are written to your `DbContext`, committed with business data |
| Saga model (`Saga` class with methods)                         | `Saga<TKey>` with `[Correlate]` + `ISagaHandler<TSaga, TMessage>` (opt-in)             |
| `LightweightSession` (Marten)                                  | Your `DbContext` — Warp doesn't bring its own ORM                                     |

## Wiring differences

### Static handlers → instance classes

Wolverine accepts static methods on a `[Handler]` or `[WolverinePost]` class. Warp's source generator binds against the `IRequestHandler<TRequest, TResponse>` interface, whose `HandleAsync(...)` method is instance-only. Static methods don't compile.

```csharp
// Wolverine — works:
public static class OrderHandler
{
    public static Task<OrderResponse> Handle(PlaceOrderRequest request, IDbContext db) { ... }
}

// Warp — required:
public sealed class OrderHandler : IRequestHandler<PlaceOrderRequest, OrderResponse>
{
    private readonly AppDbContext _db;
    public OrderHandler(AppDbContext db) => _db = db;

    public Task<OrderResponse> HandleAsync(PlaceOrderRequest request, CancellationToken ct) { ... }
}
```

The instance is resolved from DI per request. If you were relying on static-method parameter injection (Wolverine's "method as DI graph"), you'll move those parameters to the constructor.

### Cascading return values: the two-phase save

Wolverine's most distinctive idiom is `Task<MyEvent> Handle(...)` — the returned event is auto-published. Warp doesn't do that. You publish explicitly:

```csharp
public async Task<Unit> HandleAsync(ProcessOrder request, CancellationToken ct)
{
    var order = new Order { CustomerName = request.CustomerName };
    _db.Orders.Add(order);
    await _db.SaveChangesAsync(ct);    // commit primary write — get order.Id

    await _publisher.Publish(new OrderProcessed(order.Id));
    await _publisher.SaveChangesAsync(ct);   // commit outbox row

    return Unit.Value;
}
```

There's an important architectural consequence here: **with Postgres identity PKs (and SQL Server `IDENTITY` columns), this becomes a two-phase save.** You need the primary row's database-generated ID before you can construct the outbox message, which means atomicity is lost between the two `SaveChanges` calls.

Downstream consumers of the published event must therefore be **idempotent** — they can be re-delivered if the second `SaveChanges` fails after the first one commits. The standard pattern is to dedupe on a domain key (e.g. `Order.Id` here) at the destination.

If you control the ID generation (GUIDs, Snowflake, etc.) and assign it client-side, you can construct the outbox message before the first save and commit everything in a single `SaveChanges`. That's the recommended pattern when you have the flexibility.

### Custom auth policies — same `[Authorize]`, same semantics

`[Authorize(Policy = "X")]` on a `[WarpHttpPost]` handler class composes with ASP.NET's standard authorization pipeline. Custom `IAuthorizationRequirement` + `AuthorizationHandler<TRequirement>` work the same as on any minimal API endpoint — Warp surfaces the attribute as endpoint metadata via `EndpointBuilder.WithMetadata(attr)`.

Empirical test coverage (`CustomAuthorizationRequirementTests` in the Warp test suite) confirms the following are all working as designed:

- A custom `IAuthorizationHandler<TRequirement>` is invoked when the policy is evaluated, on every `[WarpHttpPost]` endpoint.
- The handler runs **even when the configured authentication scheme returns `AuthenticateResult.NoResult()`** (the common case for a webhook endpoint reached without user credentials). A correct header on the requirement still produces a 200.
- The handler runs **even when the policy combines `RequireAuthenticatedUser()` with a custom requirement** and authentication failed — the request is denied with 401, but the custom handler still gets a turn. If your handler "never fires" alongside a 401, your log capture is filtered.

If a custom policy's handler isn't firing on a `[WarpHttp*]` endpoint, check:

1. `app.UseAuthentication()` and `app.UseAuthorization()` are both wired.
2. At least one authentication scheme is registered. ASP.NET throws an `InvalidOperationException` at request time if a policy is configured but no scheme exists.
3. The policy is registered with the same name the attribute references (case-sensitive). A typo gets a clear "The AuthorizationPolicy named: 'X' was not found" exception.
4. The `IAuthorizationHandler` is registered as a service (typically `AddSingleton<IAuthorizationHandler, MyHandler>()`).
5. **No authentication scheme is calling `AuthenticateResult.Fail(...)` early.** `Fail` short-circuits the entire authorization pipeline — requirements are never evaluated. This is distinct from `NoResult()` (which leaves the user unauthenticated but lets requirements run). If your scheme calls `Fail` when it sees malformed credentials, requests with no credentials but a valid webhook secret will still get 401 without your handler running. Switch to `NoResult` for the "no credentials" path.
6. **Check for a `FallbackPolicy`.** `AuthorizationOptions.FallbackPolicy` is applied to all endpoints by default — including `[Authorize(Policy="X")]` ones. If the fallback policy calls `Fail` or `RequireAuthenticatedUser` and your scheme can't satisfy it, the request fails before your policy is reached on some configurations.

None of the above are Warp-specific — they're standard ASP.NET auth wiring. Warp doesn't intercept the auth pipeline; it just attaches the attribute to the endpoint. If you've gone through this list and the handler still doesn't fire with a `[WarpHttp*]` endpoint where the same setup works on a raw `MapPost`, please file a minimal repro and we'll dig in.

### Replacing `OnException<>().MoveToErrorQueue()`

Wolverine's exception policies are declarative. Warp's are split between an attribute (`[Retry]`) and pipeline behaviors:

```csharp
// Wolverine:
//   OnException<DomainRefusedException>().MoveToErrorQueue();

// Warp — pipeline behavior that decides per-exception:
public sealed class RefuseRetryBehavior<T> : IPipelineBehavior<T, Unit> where T : IJob
{
    private readonly IJobContext _jobContext;
    public RefuseRetryBehavior(IJobContext jobContext) => _jobContext = jobContext;

    public async Task<Unit> HandleAsync(T request, RequestHandlerDelegate<Unit> next, CancellationToken ct)
    {
        try { return await next(); }
        catch (DomainRefusedException)
        {
            _jobContext.Outcome = new JobOutcome
            {
                State = State.Failed,    // No retry — go straight to Failed
                LogMessage = "Refused by domain",
            };
            throw;
        }
    }
}
```

For "retry N times then give up," prefer the `[Retry(N)]` attribute on the handler class — it's a one-liner and composes correctly with the addon ordering rules. Drop into a behavior only when the decision is conditional on the exception type.

### Replacing `InvokeMessageAndWaitAsync` in tests

Wolverine's tracking helper lets a test publish a message and block until all spawned work is terminal. Warp doesn't ship an equivalent yet; the canonical pattern is to poll `IJobQueryService` by `TraceId`:

```csharp
async Task WaitForJobAndDescendants(IJobQueryService jobs, Guid rootJobId, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        var root = await jobs.GetJobDetailById(rootJobId);
        if (root is null) throw new InvalidOperationException("Root job vanished");

        var inFlight = await jobs.GetTraceTree(root.TraceId!.Value);
        if (inFlight.All(j => j.CurrentState is State.Completed or State.Failed or State.Deleted))
        {
            return;
        }

        await Task.Delay(100);
    }
    throw new TimeoutException();
}
```

This is the pattern teams have landed on after porting from Wolverine. A first-class `Moberg.Warp.Testing` package with this helper is a likely future addition — if you build something better, the maintainers welcome contributions.

## What doesn't have an equivalent

- **Wolverine transports for non-DB brokers** (RabbitMQ, Kafka, Azure Service Bus). Warp is intentionally database-only — that's the design trade. If your existing Wolverine deployment uses external brokers for fan-out to non-.NET services, you'll keep that broker layer outside Warp.
- **Wolverine's storage-agnostic design.** Warp is EF Core only. If your storage is Marten or raw ADO without EF, Warp isn't a drop-in.
- **Critter Stack integration with Marten event sourcing.** Warp doesn't compete with Marten's aggregate/event-sourced patterns — if those are core to your design, Wolverine is the better fit.

Warp's sweet spot is: a single .NET service (or a small cluster of them) that uses EF Core on Postgres or SQL Server, and wants durable background work + in-memory mediator + optional HTTP exposure all wired through the same `DbContext`.

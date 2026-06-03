---
sidebar_position: 3
---

# Multi-Project Source Generation

Warp's mediator dispatch and HTTP endpoints are emitted by source generators (`Warp.SourceGenerator`, `Warp.Http.SourceGenerator`) at compile time. In single-project solutions this is invisible — you write a handler, it gets registered. In multi-project solutions there are a few patterns you need to know about, because the generator scans transitively across project references and there's no out-of-the-box way to limit it without the API on this page.

## How the generator decides what to register

Each project that references `Moberg.Warp.Core` (and therefore `Warp.SourceGenerator`) gets its own pass:

1. The generator walks the current compilation **and every referenced assembly** looking for types implementing `IJob`, `IMessage`, `IRequest<>`, `IStreamRequest<>`, their handlers, and `IPipelineBehavior<,>` / `IStreamPipelineBehavior<,>` / `IPublishPipelineBehavior<>`.
2. It emits a single internal `WarpMediatorServiceExtensions` class with an `AddWarpMediator(IServiceCollection)` extension method that registers everything it found.
3. It emits a `[ModuleInitializer]` that pushes that registration onto the public `WarpGeneratedHandlerRegistry`.
4. At app startup, `AddWarp<T>` replays the registry onto your `IServiceCollection`.

This means: **every handler in every assembly your host references ends up in your host's DI graph.**

That's usually what you want. In single-host solutions, "all handlers everywhere" is convenient. In multi-host solutions (API + Workers + BackOffice all built from the same monorepo), it isn't — your API host shouldn't have to satisfy worker-only dependencies just because it transitively references the worker assembly.

## Pattern 1: Internal generated classes — no CS0436

The emitted `WarpMediatorServiceExtensions` is `internal`, not `public`. That sounds boring, but it's the entire mitigation for one specific cross-project failure: **CS0436 "duplicate type" warnings across project references**.

A public generated type with the same name in two referenced projects would force a compile-time decision about which one wins. As `TreatWarningsAsErrors=true` is the project default, that's a build break. With `internal`, neither generated class is visible across assembly boundaries — each one is callable from its own assembly, no collision.

The cross-assembly registration path doesn't need the class to be public anyway: each assembly's `[ModuleInitializer]` runs at load time and pushes its registrations to the public `WarpGeneratedHandlerRegistry`. `AddWarp<T>` then replays that registry.

You don't need to do anything to benefit from this — it's just how the generator works in the current release line. If you're upgrading from a build that emitted `WarpMediatorServiceExtensions` as a `public` type and see `CS0436` warnings go away, this is why.

## Pattern 2: Excluding handlers from a referenced assembly

When a host references an assembly whose handlers it doesn't want in its DI graph, exclude that assembly with `opt.ExcludeHandlersFromAssembly(...)`:

```csharp
// API host's Program.cs
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();

    // The API host references Domain → which is also referenced by Workers.
    // Workers defines IJobHandler<SendWelcomeEmail> with a worker-only IEmailDispatcher
    // dependency that the API host can't satisfy. Don't register handlers from Workers
    // into the API host's DI graph.
    opt.ExcludeHandlersFromAssembly(typeof(WorkersAssemblyMarker).Assembly);
});
```

What `ExcludeHandlersFromAssembly` actually does:

- The source generator still discovers and emits registrations for handlers in the excluded assembly — there's nothing the runtime can do about source-gen time.
- After `AddWarp` finishes replaying the registry, it walks the `IServiceCollection` and removes any `IRequestHandler<,>`, `IJobHandler<>`, `IMessageHandler<>`, or `IStreamRequestHandler<,>` whose `ImplementationType.Assembly` is in the excluded list.
- Pipeline behaviors and other DI registrations are **not** removed — exclusion is scoped to handlers.

The exclusion is per-host: your API host can exclude Workers, your Workers host can exclude BackOffice, etc. Each `AddWarp` lambda configures its own.

### When to use it

- Multi-host monorepo where hosts pull in each other's handlers transitively and you don't want cross-host DI satisfaction.
- A library project that exports types deliberately *not* meant to be auto-registered as handlers in the consuming app.

### When it's a smell

- If you're excluding every assembly except one, you probably want to split the solution differently — perhaps move shared handler types into a leaf assembly and let the hosts reference *that* directly.
- If the same handler should be registered in two hosts but with different dependencies, that's a design issue exclusion won't solve cleanly. Use distinct handler types per host.

## Pattern 3: `IJob` types cannot also be `[WarpHttp*]` request bodies

The `Warp.Http.SourceGenerator` rejects a type that implements both `IJob` (or `IMessage`) and is wired to a `[WarpHttpPost]` / `[WarpHttpGet]` / etc. attribute. The diagnostic is `WHTTP001`.

The rationale: HTTP endpoints expose a synchronous request/response contract. `IJob` is durable, retried, scheduled, and asynchronous. Conflating the two means a single request body shape that's simultaneously a transient HTTP DTO and a persisted job record — which leaks job-orchestration concerns into your HTTP contract and HTTP-shape concerns into your durable store.

If you want "submit via HTTP, process as job," use **two** types:

```csharp
// HTTP request DTO — what the API contract is.
public sealed record QueueEmailRequest(int EmailLogId) : IRequest<Guid>;

[WarpHttpPost("/http/queue-email")]
public sealed class QueueEmailHandler : IRequestHandler<QueueEmailRequest, Guid>
{
    private readonly IPublisher _publisher;
    public QueueEmailHandler(IPublisher publisher) => _publisher = publisher;

    public async Task<Guid> HandleAsync(QueueEmailRequest request, CancellationToken ct)
    {
        // The job — what gets persisted and retried.
        var id = await _publisher.Enqueue(new SendEmailJob { EmailLogId = request.EmailLogId });
        await _publisher.SaveChangesAsync(ct);
        return id;
    }
}

public sealed record SendEmailJob : IJob
{
    public int EmailLogId { get; init; }
}
```

The HTTP DTO is shaped for the API, the job is shaped for processing, and they're explicitly mapped between by the handler. If they diverge over time (you add tenant resolution, schema versioning, etc.) you mutate them independently rather than fighting one type doing both jobs.

## Pattern 4: `[WarpHttp*]` requires an instance class

A class implementing `IRequestHandler<TRequest, TResponse>` (or `IStreamRequestHandler<,>`) — the interface methods are instance methods, so static handler classes don't compile against the contract. Coming from frameworks where HTTP endpoints can be static methods (Wolverine, FastEndpoints in some modes), this is a constraint to know up front.

The instance is resolved from DI per request. It can have constructor-injected dependencies — `IPublisher`, your own services, logging, etc. — and is disposed at request scope teardown.

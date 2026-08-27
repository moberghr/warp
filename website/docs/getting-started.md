---
sidebar_position: 1
---

# Getting Started

Warp is a distributed job processing and message queue library for .NET 10. It provides four patterns:

- **[Messages](./patterns/messages.md)** (`IMessage`) — Pub/sub queue. Multiple handlers per message, each becomes an independent job.
- **[Jobs](./patterns/jobs.md)** (`IJob`) — Orchestrated background work. Single handler, scheduling, retries, continuations, batches.
- **[Requests](./patterns/requests.md)** (`IRequest<TResponse>`) — In-memory request/response. Single handler, no persistence, returns a typed response via `IMediator.Send()`.
- **[Streams](./patterns/requests.md#streams)** (`IStreamRequest<TResponse>`) — In-memory streaming. Single handler, no persistence, returns `IAsyncEnumerable<TResponse>` via `IMediator.CreateStream()`.

## Installation

```bash
dotnet add package Moberg.Warp.Core                  # Publisher + mediator
dotnet add package Moberg.Warp.Provider.PostgreSql   # PostgreSQL provider (or SqlServer)
dotnet add package Moberg.Warp.Worker                # Worker service
dotnet add package Moberg.Warp.Dashboard             # Dashboard
dotnet add package Moberg.Warp.Http                  # HTTP exposure for IRequest/IStreamRequest (optional)
```

## Setup

### 1. Register your DbContext

Register your DbContext as usual — no special configuration needed:

```csharp
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
```

Warp automatically adds its interceptors (row locking) and entity configuration (Job, Message, Batch, etc.) when you register Warp services in the next step. All Warp tables are placed in the `warp` schema by default.

:::tip Naming Conventions
Warp respects EF Core naming conventions. If you use `UseSnakeCaseNamingConvention()`, Warp's tables and columns will follow your convention automatically.
:::

### 2. Create the database schema

Warp adds its tables (Job, JobLog, Server, Worker, etc.) to your DbContext model automatically. You just need to create the schema.

**Using EF Core migrations** (recommended for production):

```bash
dotnet ef migrations add AddWarp
dotnet ef database update
```

The migration will include all Warp tables alongside your own. When you upgrade the Warp NuGet package and the schema has changed, just add a new migration:

```bash
dotnet ef migrations add UpgradeWarp
dotnet ef database update
```

EF Core detects the model diff automatically — no manual SQL needed. Warp upgrades are **additive** — new features add tables and (nullable) columns to your model, which your normal `dotnet ef migrations add` / `dotnet ef database update` flow picks up alongside your own changes. Opt-in features stay dormant until you enable them: [multi-application observability](./features/applications.md), for example, adds its two tables and provenance columns to the schema unconditionally but does nothing at runtime until you set `opt.ApplicationName`.

**Using EnsureCreatedAsync** (quick start / development):

```csharp
var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
await context.Database.EnsureCreatedAsync();
```

:::warning EnsureCreated doesn't support upgrades
`EnsureCreatedAsync()` creates the schema from scratch but cannot apply incremental changes. Use EF migrations for production deployments where you need to upgrade Warp versions without dropping the database.
:::

### 3. Register Warp

```csharp
// Publisher only — for apps that create jobs but don't process them
builder.Services.AddWarp<AppDbContext>(opt =>
{
    opt.UsePostgreSql();
});
```

:::tip Handler registration is automatic
Handlers are discovered and registered automatically via the Warp source generator — no `AddHandlers()` call needed.
:::

:::tip TimeProvider
Warp automatically registers `TimeProvider.System` if one is not already registered. Override it in tests to control time.
:::

### 4. Add a worker (optional)

For apps that process jobs, use `AddWarpServer` instead (includes `AddWarp` internally):

```csharp
builder.Services.AddWarpServer<AppDbContext>(options =>
{
    options.UsePostgreSql();
    options.WorkerCount = 10;
    options.Queues = ["default", "critical"];

    // Optional addons — call inside the same lambda
    options.AddRetry(o =>
    {
        o.MaxRetries = 3;
        o.Delays = [15, 60, 300]; // seconds
    });
});
```

### 5. Add the dashboard (optional)

```csharp
app.MapWarpDashboard("/warp");
```

The dashboard is a set of routed endpoints, so gate it with your own authorization policy:

```csharp
app.MapWarpDashboard("/warp").RequireAuthorization("WarpDashboard");
```

See [Dashboard Auth](/docs/operations/dashboard-auth) for the built-in login and localhost-only options.

### 6. Define handlers

```csharp
public class SendEmailRequest : IJob { public string Email { get; set; } }

public class SendEmailHandler : IJobHandler<SendEmailRequest>
{
    public async Task HandleAsync(SendEmailRequest message, CancellationToken ct)
    {
        // Send the email
    }
}
```

### 7. Define a request (optional)

```csharp
public class GetUser : IRequest<UserDto> { public int UserId { get; set; } }

public class GetUserHandler : IRequestHandler<GetUser, UserDto>
{
    public async Task<UserDto> HandleAsync(GetUser request, CancellationToken ct)
    {
        // Query and return
    }
}
```

### 8. Define a stream (optional)

```csharp
public class GetUsers : IStreamRequest<UserDto> { public string Role { get; set; } }

public class GetUsersHandler : IStreamRequestHandler<GetUsers, UserDto>
{
    public async IAsyncEnumerable<UserDto> HandleAsync(GetUsers request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Yield items one at a time
    }
}
```

### 9. Publish & Send

```csharp
public class OrderController : ControllerBase
{
    private readonly IPublisher _publisher;
    private readonly IMediator _mediator;
    private readonly AppDbContext _context;

    public async Task<IActionResult> CreateOrder(Order order)
    {
        _context.Orders.Add(order);

        // Job is created in the same DbContext — committed atomically (outbox pattern)
        await _publisher.Enqueue(new SendEmailRequest { Email = order.Email });

        // Single SaveChangesAsync commits both the order and the job
        await _context.SaveChangesAsync();
        return Ok();
    }

    public async Task<IActionResult> GetUser(int id)
    {
        // In-memory request — no database persistence, immediate response
        var user = await _mediator.Send(new GetUser { UserId = id });
        return Ok(user);
    }
}
```

:::info Transactional Outbox
Warp uses the [outbox pattern](/docs/features/outbox-pattern) — jobs are written to the same DbContext as your business data and committed in a single `SaveChangesAsync()`. This guarantees atomicity: if the transaction fails, both your data and the jobs roll back. No orphaned jobs, no lost work.
:::

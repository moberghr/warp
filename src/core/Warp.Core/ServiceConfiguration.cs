using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Adapters;
using Warp.Core.BackgroundServices;
using Warp.Core.Data.Converters;
using Warp.Core.Data.Entities;
using Warp.Core.Data.Queries;
using Warp.Core.Diagnostics;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Events;
using Warp.Core.Handlers;
using Warp.Core.Interceptors;
using Warp.Core.Logging;
using Warp.Core.Notifications;
using Warp.Core.Notifiers;
using Warp.Core.Services;
using Warp.Core.Webhooks;

namespace Warp.Core;

public static class ServiceConfiguration
{
    private static readonly SaveChangesConcurrencyTokenInterceptor _saveChangesInterceptor = new();

    /// <summary>
    /// Registers Warp's publish-side services against the user's <typeparamref name="TContext"/>:
    /// <c>IPublisher</c>, <c>IMediator</c>, <c>IRecurringJobPublisher</c>, the query services, and
    /// the EF Core model customizer / row-lock interceptors. Use this for processes that only
    /// publish or serve the dashboard; call <c>AddWarpServer</c> instead (it calls this internally)
    /// for processes that also execute jobs. <typeparamref name="TContext"/> must already be
    /// registered via <c>AddDbContext</c> (scoped). Opt into a provider — <c>opt.UsePostgreSql()</c>
    /// or <c>opt.UseSqlServer()</c> — and any addons from the <paramref name="configure"/> lambda.
    /// Handlers and pipeline behaviors are discovered by the source generator; there is no
    /// <c>AddHandlers</c> call.
    /// </summary>
    public static IServiceCollection AddWarp<TContext>(
        this IServiceCollection services,
        Action<WarpBuilder<TContext>>? configure = null)
        where TContext : DbContext
    {
        EnsureDbContextRegisteredAsScoped<TContext>(services);

        var builder = new WarpBuilder<TContext>(services);
        configure?.Invoke(builder);

        // The builder IS the configuration. Register it as the IOptions<WarpConfiguration>
        // value so downstream consumers (JobCommandService, WarpModelCustomizer, etc.) see
        // exactly what the caller set, and so addon-contributed EntityConfigurators survive.
        // TryAdd: if the caller has already registered IOptions<WarpConfiguration> (e.g. via
        // AddWarpServer, which inherits WarpConfiguration), keep theirs.
        services.TryAddSingleton<IOptions<WarpConfiguration>>(Options.Create<WarpConfiguration>(builder));

        var configured = CreateWarpServices<TContext>(services);

        // Non-server application heartbeat host — registered ONLY when this process opted into
        // multi-application observability (ApplicationName set). A deliberate change to the passive
        // AddWarp contract (§2.13): a publisher/API/dashboard-only process now writes an
        // ApplicationInstance row so it is visible in the Applications view. It self-inerts in server
        // processes (the IWarpServerPresence marker is present) — those record themselves on Server.
        // Null ApplicationName ⇒ the host is not registered at all, so behavior is byte-for-byte unchanged.
        if (builder.ApplicationName is not null)
        {
            // Stamp the process-wide origin for cross-application traces (§ tracing). A static is fine:
            // ApplicationName is a deploy-time constant for the process, so every Warp-created Activity in
            // it carries the same warp.application tag. Null ApplicationName ⇒ this is never set and the
            // factories add no tag (feature off).
            WarpTelemetry.ApplicationName = builder.ApplicationName;

            // Shared CPU/RAM sampler (also registered by AddWarpServer). TryAdd so the two paths don't
            // fight when a process calls both AddWarp and AddWarpServer.
            configured.TryAddSingleton<ProcessCpuTracker>();

            // Registered only once per TContext — a second AddWarp<TContext> call must not add a second
            // heartbeat host (which would insert a second ApplicationInstance row for the one process).
            // Mirrors the BackgroundServiceHost guard in Warp.Worker/ServiceConfiguration.
            if (!configured.Any(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(ApplicationHeartbeatHost<TContext>)))
            {
                configured.AddHostedService<ApplicationHeartbeatHost<TContext>>();
            }
        }

        return configured;
    }

    private static IServiceCollection CreateWarpServices<TContext>(this IServiceCollection services)
        where TContext : DbContext
    {
        ConfigureDbContextOptions<TContext>(services);

        services.TryAddSingleton(TimeProvider.System);

        WarpGeneratedHandlerRegistry.ApplyAll(services);
        RemoveExcludedHandlerRegistrations(services);
        ValidateAddonAttributesOnHandlers(services);

        services.AddScoped<IPublisher>(x => new Publisher<TContext>(
            x.GetRequiredService<TContext>(),
            x.GetRequiredService<IOptions<WarpConfiguration>>(),
            x.GetRequiredService<TimeProvider>(),
            x,
            x.GetRequiredService<IWarpNotificationTransport>(),
            x.GetRequiredService<ServerTaskSignals<TContext>>()));

        services.AddScoped<IMediator>(x => new Mediator(x));

        services.AddScoped<IRecurringJobPublisher>(x =>
            new RecurringJobPublisher<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>(), x.GetRequiredService<IWarpLockProvider>()));
        services.AddScoped<IJobQueryService>(x => new JobQueryService<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>()));
        services.AddScoped<IJobCommandService>(x => new JobCommandService<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>(), x.GetRequiredService<IOptions<WarpConfiguration>>(), x.GetRequiredService<IWarpNotificationTransport>(), x.GetRequiredService<IWarpSqlQueries<TContext>>(), x.GetRequiredService<ServerTaskSignals<TContext>>()));
        services.AddScoped<IJobGroupQueryService>(x => new JobGroupQueryService<TContext>(x.GetRequiredService<TContext>()));
        services.AddScoped<IRecurringJobService>(x => new RecurringJobService<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>(), x.GetRequiredService<IWarpNotificationTransport>(), x.GetRequiredService<ServerTaskSignals<TContext>>()));
        services.AddScoped<IDashboardStatsService>(x => new DashboardStatsService<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>()));
        services.AddScoped<IServerCommandService>(x => new ServerCommandService<TContext>(x.GetRequiredService<TContext>(), x.GetRequiredService<TimeProvider>()));
        services.AddScoped<IBatchPublisher>(x => new BatchPublisher<TContext>(
            x.GetRequiredService<TContext>(),
            x.GetRequiredService<IOptions<WarpConfiguration>>(),
            x.GetRequiredService<TimeProvider>(),
            x,
            x.GetRequiredService<IWarpNotificationTransport>(),
            x.GetRequiredService<ServerTaskSignals<TContext>>()));

        services.AddScoped<JobContext>();
        services.AddScoped<IJobContext>(x => x.GetRequiredService<JobContext>());

        // Background-services dashboard read service. Registered in AddWarp (not AddWarpServer)
        // so dashboard-only / publisher-only processes that call AddWarp without AddWarpServer
        // can still serve the /api/services endpoints. Only depends on TContext.
        services.TryAddScoped<IBackgroundServiceQueryService, BackgroundServiceQueryService<TContext>>();

        // Adapters dashboard read service. Registered in AddWarp (not AddAdapters) so dashboard-only /
        // publisher-only processes that never call AddAdapters() can still serve the /api/adapters
        // endpoints (§2.14 stays-on-TContext). Only depends on TContext. The adapter tables are always
        // in the schema (§2.11); AddAdapters() gates recording services + the addons flag only.
        services.TryAddScoped<IAdapterQueryService, AdapterQueryService<TContext>>();

        // Applications dashboard read service (§8.19 multi-app observability). Registered in AddWarp (not
        // gated on ApplicationName) so dashboard-only / publisher-only processes serve /api/applications
        // without running a server — it unifies the Server + ApplicationInstance tables (both always in the
        // schema §2.11) into one roster and resolves on TContext (§2.14 stays-on-TContext).
        services.TryAddScoped<IApplicationQueryService>(x => new ApplicationQueryService<TContext>(
            x.GetRequiredService<TContext>(),
            x.GetRequiredService<TimeProvider>(),
            x.GetRequiredService<IOptions<WarpConfiguration>>()));

        // Adapter call-scope primitive is always available (§2.15 telemetry is unconditional): every
        // BeginCall emits its Activity + meters, and manual scopes (e.g. the webhook executor) can record
        // regardless of AddAdapters(). AddAdapters() runs earlier in the AddWarp lambda and TryAdds the real
        // DbAdapterCallRecorder + flusher, so it wins; absent it, the null recorder discards rows while
        // telemetry still flows. The dashboard "adapters" flag keys on DbAdapterCallRecorder, not this.
        services.TryAddSingleton<AdapterRegistry>();
        services.TryAddSingleton<IAdapterCallRecorder, NullAdapterCallRecorder>();
        services.TryAddSingleton<IWarpAdapters, WarpAdapters>();

        // Inbound endpoint observability dashboard read service. Registered in AddWarp (not
        // AddEndpointObservability) so dashboard-only / publisher-only processes can serve /api/endpoints
        // without running the middleware. The EndpointCallLog table is always in the schema (§2.11), so
        // AddEndpointObservability() gates only the recorder/flusher/middleware plus the addons flag.
        services.TryAddScoped<IEndpointQueryService, EndpointQueryService<TContext>>();

        // Client (browser) observability read service — registered by AddWarp (like IEndpointQueryService) so
        // dashboard-only / publisher-only processes serve /api/client without running the ingest endpoint. The
        // ClientEventLog table is always in the schema (§2.11); AddClientObservability() gates only the
        // recorder/flusher/ingest endpoint plus the addons flag (§8.27).
        services.TryAddScoped<IClientEventQueryService, ClientEventQueryService<TContext>>();

        // Unified trace view (§8.28) — one screen for a trace id, unioned from the rows Warp already persists
        // (client request + endpoint call + jobs + adapter calls). Registered by AddWarp so dashboard-only
        // processes resolve it; no new storage or span collector.
        services.TryAddScoped<ITraceQueryService, TraceQueryService<TContext>>();

        // Webhooks dashboard read + redeliver command services. Registered in AddWarp (not AddWebhooks) so
        // dashboard-only / publisher-only processes that never call AddWebhooks() can still serve the
        // /api/webhooks endpoints (§2.14 stays-on-TContext). The WebhookDelivery table is always in the
        // schema (§2.11); AddWebhooks() gates the executor/dispatcher + the addons flag only.
        services.TryAddScoped<IWebhookQueryService, WebhookQueryService<TContext>>();
        services.TryAddScoped<IWebhookCommandService, WebhookCommandService<TContext>>();

        // Webhook delivery engine (part of Core, §8.20) wired unconditionally: dispatcher, executor job
        // handler, redelivery enqueuer, and the built-in signer. Always-on so any AddWarpServer drains
        // warp:webhooks with no per-process opt-in to forget — nothing runs until SendAsync stages a
        // delivery. Optional host hooks (custom signer, exhausted handler) come from opt.AddWebhooks(...).
        services.TryAddScoped<IWebhookDispatcher, WebhookDispatcher<TContext>>();
        services.TryAddScoped<IJobHandler<ExecuteWebhookDelivery>, ExecuteWebhookDeliveryHandler<TContext>>();
        services.TryAddScoped<IWebhookRedeliveryEnqueuer, WebhookRedeliveryEnqueuer>();
        services.TryAddSingleton<StandardWebhooksSigner>();

        // The webhook executor resolves IHttpClientFactory (the warp-webhooks named client) — and the handler
        // above is registered unconditionally, so Core MUST supply the factory. Without this, a plain AddWarp
        // process (no AddAdapters) has an unresolvable handler that fails ValidateOnBuild — breaking `dotnet ef`
        // and ASP.NET Core startup in Development (both build the provider with validation on). AddHttpClient is
        // additive/idempotent: a host that configures its own "warp-webhooks" client (resilience, proxy) keeps
        // that config; this only guarantees the factory + named client exist.
        services.AddHttpClient(WebhookConstants.AdapterName);

        // Recording config for the warp-webhooks adapter every attempt is logged under (§8.20): response
        // bodies always captured (diagnosis), request bodies never (payload is on the row), call-log
        // retention aligned to the delivery retention, grouped by endpoint. Folded into AdapterRegistry so
        // the executor's manual scope + the flusher resolve it; a row is written only where AddAdapters() ran.
        services.AddSingleton(x =>
        {
            var config = x.GetRequiredService<IOptions<WarpConfiguration>>().Value;

            return new AdapterRegistrationEntry(
                WebhookConstants.AdapterName,
                new WarpAdapterOptions
                {
                    CaptureResponseBodies = CaptureMode.Always,
                    CaptureRequestBodies = CaptureMode.None,
                    GroupLabel = "Endpoint",
                    CallLogRetention = config.WebhookDeliveryRetention,
                },
                ConfigSummary: null);
        });

        // Default no-op transport. opt.UseDatabasePush() (inside the AddWarp/AddWarpServer lambda) replaces this with a
        // provider-specific implementation (Postgres LISTEN/NOTIFY or SQL Server Service Broker).
        services.TryAddSingleton<IWarpNotificationTransport, NullNotificationTransport>();

        // In-process signal bus. Registered here (not in AddWarpServer) so Core-side publishers
        // — IPublisher, IBatchPublisher, IJobCommandService, IRecurringJobService, SagaStore —
        // can inject it from publish-only processes that never call AddWarpServer. Worker-side
        // server-task loops and the dashboard broadcaster subscribe to its channels at host
        // construction; with no subscribers the SignalXxx calls are cheap no-ops.
        services.TryAddSingleton<ServerTaskSignals<TContext>>();

        // Operational-event notifier fan-out. Registered by AddWarp (not AddWarpServer) so every dispatch
        // site — the webhook executor, the saga command service, the server-side stale sweep — resolves it,
        // and so it works in any AddWarp process. Non-generic + guarded: with no IWarpNotifier registered
        // (opt.AddNotifier<T>()), DispatchAsync is a cheap no-op. See Warp.Core.Notifiers.
        services.TryAddSingleton<WarpNotifierDispatcher>();

        // Fail-fast model validation at host startup (plain IHostedService → awaited to completion
        // before the app starts). AddHostedService dedups via TryAddEnumerable, so the second AddWarp
        // call from AddWarpServer's AddServerHostCore doesn't double-register. The Publisher /
        // BatchPublisher constructor guard backstops non-hosted (raw ServiceProvider) usage.
        services.AddHostedService<WarpModelValidationService<TContext>>();

        // IWarpSqlQueries<TContext> is registered by the provider package (Warp.PostgreSql /
        // Warp.SqlServer) via their UsePostgreSql / UseSqlServer builder extensions.
        return services;
    }

    // Fails fast when TContext isn't registered as Scoped. The common cause is the user
    // calling AddDbContextFactory<TContext> instead of AddDbContext<TContext>: the factory
    // overload registers IDbContextFactory<TContext> but not TContext itself, so every
    // scoped Warp service that takes TContext via constructor injection blows up at first
    // resolve. Catching this at AddWarp time turns a silent runtime crash into a clear
    // startup error with the fix in the message.
    private static void EnsureDbContextRegisteredAsScoped<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(TContext)) ?? throw new InvalidOperationException(
                $"AddWarp<{typeof(TContext).Name}>() requires {typeof(TContext).Name} to be registered " +
                $"via services.AddDbContext<{typeof(TContext).Name}>(...). If you're using " +
                $"AddDbContextFactory<{typeof(TContext).Name}>(...) (e.g. for Blazor / design-time tooling), " +
                $"also call AddDbContext<{typeof(TContext).Name}>(...) so Warp's scoped services can resolve " +
                "the context. For design-time tooling (dotnet ef migrations), the migrations host must " +
                "use a real Host builder that calls AddDbContext — Warp's model customizer wires in via " +
                $"DbContextOptions<{typeof(TContext).Name}>, which AddDbContextFactory does not expose to " +
                "design-time tooling.");
        if (descriptor.Lifetime != ServiceLifetime.Scoped)
        {
            throw new InvalidOperationException(
                $"AddWarp<{typeof(TContext).Name}>() requires a Scoped {typeof(TContext).Name} registration; " +
                $"got {descriptor.Lifetime}. AddDbContext<TContext>(...) registers Scoped by default — " +
                "do not override the lifetime when using Warp.");
        }
    }

    // Strips IRequestHandler / IJobHandler / IMessageHandler / IStreamRequestHandler
    // registrations whose implementation type lives in an excluded assembly. Called once
    // after WarpGeneratedHandlerRegistry.ApplyAll. No-op when no assemblies are excluded.
    private static void RemoveExcludedHandlerRegistrations(IServiceCollection services)
    {
        var optionsDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IOptions<WarpConfiguration>));
        if (optionsDescriptor?.ImplementationInstance is not IOptions<WarpConfiguration> optionsInstance)
        {
            return;
        }

        var excluded = optionsInstance.Value.ExcludedHandlerAssemblies;
        if (excluded.Count == 0)
        {
            return;
        }

        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (!descriptor.ServiceType.IsGenericType)
            {
                continue;
            }

            var def = descriptor.ServiceType.GetGenericTypeDefinition();
            if (def != typeof(Handlers.IRequestHandler<,>)
                && def != typeof(Handlers.IJobHandler<>)
                && def != typeof(Handlers.IMessageHandler<>)
                && def != typeof(Handlers.IStreamRequestHandler<,>))
            {
                continue;
            }

            var implType = descriptor.ImplementationType;
            if (implType is null)
            {
                continue;
            }

            if (excluded.Contains(implType.Assembly))
            {
                services.RemoveAt(i);
            }
        }
    }

    // #242: [Timeout] / [Mutex] / [Semaphore] / [RateLimit] are read only from the request/job type — the
    // publish behavior stamps them into metadata at enqueue, where the handler type is not yet known, and
    // the execution behaviors read only that metadata. Placed on a handler class they compile (the
    // attributes target Class) but are a SILENT no-op. Fail loudly at registration so the misplacement is
    // obvious instead of a job that quietly never times out / never serializes. (Retry and CircuitBreaker
    // additionally resolve a handler-level attribute at execution, so they are not rejected here; the
    // universally-safe placement for every addon is the request/job type.)
    internal static void ValidateAddonAttributesOnHandlers(IServiceCollection services)
    {
        var handlerDefinitions = new[]
        {
            typeof(Handlers.IRequestHandler<,>),
            typeof(Handlers.IJobHandler<>),
            typeof(Handlers.IMessageHandler<>),
            typeof(Handlers.IStreamRequestHandler<,>),
        };

        var requestOnlyAttributes = new[]
        {
            typeof(Timeout.TimeoutAttribute),
            typeof(Concurrency.MutexAttribute),
            typeof(Concurrency.SemaphoreAttribute),
            typeof(RateLimit.RateLimitAttribute),
        };

        var handlers = services
            .Where(x => !x.IsKeyedService) // ImplementationType getter throws for keyed descriptors
            .Where(x => x.ServiceType.IsGenericType)
            .Where(x => handlerDefinitions.Contains(x.ServiceType.GetGenericTypeDefinition()))
            .Where(x => x.ImplementationType is not null)
            .Select(x => new { Handler = x.ImplementationType!, Request = x.ServiceType.GetGenericArguments()[0] })
            .Distinct();

        foreach (var entry in handlers)
        {
            // Self-handling job (e.g. `class Foo : IJob, IJobHandler<Foo>`): the impl type IS the request
            // type, so an addon attribute on it is the correct request-axis placement, not a misplaced
            // handler attribute. Don't reject it.
            if (entry.Handler == entry.Request)
            {
                continue;
            }

            foreach (var attribute in requestOnlyAttributes)
            {
                if (entry.Handler.GetCustomAttributes(attribute, inherit: false).Length == 0)
                {
                    continue;
                }

                var name = attribute.Name.Replace("Attribute", string.Empty, StringComparison.Ordinal);

                throw new InvalidOperationException(
                    $"[{name}] is declared on handler '{entry.Handler.Name}', where it is silently ignored: {name} is "
                    + "read only from the request/job type (stamped into metadata at publish, before the handler "
                    + "is known). Move the attribute to the request/job type. See issue #242.");
            }
        }
    }

    private static void ConfigureDbContextOptions<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(DbContextOptions<TContext>));
        if (descriptor?.ImplementationFactory == null)
        {
            return;
        }

        var originalFactory = descriptor.ImplementationFactory;
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(
            typeof(DbContextOptions<TContext>),
            sp =>
            {
                var options = (DbContextOptions<TContext>)originalFactory(sp);
                var builder = new DbContextOptionsBuilder<TContext>(options);
                builder.AddWarpInterceptors();
                builder.ReplaceService<IModelCustomizer, WarpModelCustomizer>();
                return builder.Options;
            },
            descriptor.Lifetime));
    }

    public static DbContextOptionsBuilder AddWarpInterceptors(this DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(_saveChangesInterceptor);

        return optionsBuilder;
    }

    public static void AddOutboxStateEntity(this ModelBuilder modelBuilder, string? schema = "warp")
    {
        AddJobEntity(modelBuilder, schema);
        AddRecurringJobEntity(modelBuilder, schema);
        AddRecurringJobLogEntity(modelBuilder, schema);
        AddServerEntity(modelBuilder, schema);
        AddWorkerEntity(modelBuilder, schema);
        AddWorkerGroupEntity(modelBuilder, schema);
        AddJobLogEntity(modelBuilder, schema);
        AddStatisticEntity(modelBuilder, schema);
        AddCounterEntity(modelBuilder, schema);
        AddServerTaskEntity(modelBuilder, schema);
        AddServerLogEntity(modelBuilder, schema);
        AddBackgroundServiceDefinitionEntity(modelBuilder, schema);
        AddBackgroundServiceInstanceEntity(modelBuilder, schema);
        AddBackgroundServiceLeaseEntity(modelBuilder, schema);
        AddBackgroundServiceLogEntity(modelBuilder, schema);
        AddAdapterDefinitionEntity(modelBuilder, schema);
        AddAdapterCallLogEntity(modelBuilder, schema);
        AddWebhookDeliveryEntity(modelBuilder, schema);
        AddEndpointCallLogEntity(modelBuilder, schema);
        AddClientEventLogEntity(modelBuilder, schema);
        AddApplicationInstanceEntity(modelBuilder, schema);
        AddApplicationInstanceLogEntity(modelBuilder, schema);
        AddErrorGroupEntity(modelBuilder, schema);
        AddErrorOccurrenceEntity(modelBuilder, schema);
    }

    private static void AddJobEntity(ModelBuilder modelBuilder, string? schema)
    {
        var job = modelBuilder.Entity<Job>();

        job.Property(p => p.Id);
        job.HasKey(p => p.Id);

        job.Property(p => p.Kind);
        job.Property(p => p.Type);
        job.Property(p => p.Message);
        job.Property(p => p.CreateTime);
        job.Property(p => p.ScheduleTime);
        job.Property(p => p.CurrentState);
        job.Property(p => p.Queue);
        job.Property(p => p.ParentJobId);
        job.Property(p => p.HandlerType);
        job.Property(p => p.ExpireAt);
        job.Property(p => p.LastKeepAlive);
        job.Property(p => p.TraceId);
        job.Property(p => p.SpawnedByJobId);
        job.Property(p => p.JobCount);
        job.Property(p => p.ContinuationOptions);

        job.HasMany(x => x.ChildJobs)
            .WithOne(x => x.ParentJob)
            .HasForeignKey(x => x.ParentJobId);

        // Worker fetch: Kind + State + Queue + ScheduleTime
        job.HasIndex(p => new { p.Kind, p.CurrentState, p.Queue, p.ScheduleTime });

        // Child job queries + failed children check during completion
        job.HasIndex(p => new { p.ParentJobId, p.CurrentState });

        // Message/Batch listing pages
        job.HasIndex(p => new { p.Kind, p.CurrentState, p.CreateTime });

        job.HasIndex(p => p.ExpireAt);
        job.HasIndex(p => p.TraceId);

        job.Property(p => p.CancellationMode);

        job.Property(p => p.Metadata);

        job.Property(p => p.Application).HasMaxLength(200);

        job.Metadata.SetSchema(schema);
    }

    private static void AddRecurringJobEntity(ModelBuilder modelBuilder, string? schema)
    {
        var recurringJob = modelBuilder.Entity<RecurringJob>();

        recurringJob.Property(p => p.Id);
        recurringJob.HasKey(p => p.Id);

        recurringJob.Property(p => p.Name);
        recurringJob.HasIndex(p => p.Name).IsUnique();

        recurringJob.Property(p => p.Cron);
        recurringJob.Property(p => p.Queue);
        recurringJob.Property(p => p.CreatedAt);
        recurringJob.Property(p => p.NextExecution);
        recurringJob.Property(p => p.LastExecution);

        recurringJob.Property(p => p.DisabledAt);

        recurringJob.Property(p => p.Version).IsConcurrencyToken();

        recurringJob.Metadata.SetSchema(schema);
    }

    private static void AddRecurringJobLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<RecurringJobLog>();

        log.Property(p => p.Id);
        log.HasKey(p => p.Id);

        log.Property(p => p.RecurringJobId);
        log.Property(p => p.JobId);
        log.Property(p => p.Skipped);
        log.Property(p => p.CreatedAt);

        log.HasIndex(p => p.RecurringJobId);

        log.HasIndex(p => p.JobId);
        log.HasOne(p => p.Job).WithMany().HasForeignKey(p => p.JobId).OnDelete(DeleteBehavior.SetNull);

        log.Metadata.SetSchema(schema);
    }

    private static void AddServerEntity(ModelBuilder modelBuilder, string? schema)
    {
        var server = modelBuilder.Entity<Server>();

        server.Property(p => p.Id);
        server.HasKey(p => p.Id);

        server.Property(p => p.StartedTime);

        server.Property(p => p.LastHeartbeatTime);

        server.Property(p => p.ServiceCount);

        server.Property(p => p.PausedAt);

        server.Property(p => p.Application).HasMaxLength(200);
        server.Property(p => p.Version).HasMaxLength(200);
        server.Property(p => p.Environment).HasMaxLength(200);

        server.Metadata.SetSchema(schema);
    }

    private static void AddWorkerEntity(ModelBuilder modelBuilder, string? schema)
    {
        var worker = modelBuilder.Entity<Worker>();

        worker.Property(p => p.Id);
        worker.HasKey(p => p.Id);

        worker.Property(p => p.ServerId);
        worker.Property(p => p.StartedTime);
        worker.Property(p => p.LastHeartbeatTime);
        worker.Property(p => p.WorkerGroupId);

        worker.HasOne(p => p.Server)
            .WithMany()
            .HasForeignKey(p => p.ServerId);

        worker.HasOne(p => p.WorkerGroup)
            .WithMany()
            .HasForeignKey(p => p.WorkerGroupId);

        worker.Metadata.SetSchema(schema);
    }

    private static void AddWorkerGroupEntity(ModelBuilder modelBuilder, string? schema)
    {
        var wg = modelBuilder.Entity<WorkerGroup>();

        wg.Property(p => p.Id);
        wg.HasKey(p => p.Id);

        wg.Property(p => p.ServerId);
        wg.Property(p => p.WorkerCount);
        wg.Property(p => p.Queues);
        wg.Property(p => p.PollingIntervalMs);
        wg.Property(p => p.PausedAt);

        wg.HasOne(p => p.Server)
            .WithMany()
            .HasForeignKey(p => p.ServerId);

        wg.Metadata.SetSchema(schema);
    }

    private static void AddJobLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var jobLog = modelBuilder.Entity<JobLog>();

        jobLog.Property(p => p.Id);
        jobLog.HasKey(p => p.Id);

        jobLog.Property(p => p.JobId);
        jobLog.Property(p => p.EventType);
        jobLog.Property(p => p.Timestamp);
        jobLog.Property(p => p.Level);
        jobLog.Property(p => p.Message);
        jobLog.Property(p => p.Exception);
        jobLog.Property(p => p.DurationMs);
        jobLog.Property(p => p.WorkerId);
        jobLog.Property(p => p.Name).HasMaxLength(100);
        jobLog.Property(p => p.Value);

        // Composite index serving two query shapes:
        //   1. WHERE job_id = X — the by-job log listing on the detail page. Leading-column
        //      scan on the composite covers this; no separate (job_id) index needed.
        //   2. WHERE job_id = X AND event_type IN ('Completed','Failed','Deleted') ORDER BY
        //      timestamp DESC — the correlated subquery in
        //      JobQueryService.OrderByFinishedTimeDescending. Without the trailing columns
        //      the planner would table-scan the per-job slice and filter+sort in memory,
        //      which scales poorly for jobs with many log rows (handler logs, progress
        //      reports, retries).
        jobLog.HasIndex(p => new { p.JobId, p.EventType, p.Timestamp });

        jobLog.Metadata.SetSchema(schema);
    }

    private static void AddStatisticEntity(ModelBuilder modelBuilder, string? schema)
    {
        var stat = modelBuilder.Entity<Statistic>();

        stat.Property(p => p.Key);
        stat.HasKey(p => p.Key);
        stat.Property(p => p.Value);

        // No seed data — Statistic rows are created by the counter aggregator on demand.
        stat.Metadata.SetSchema(schema);
    }

    private static void AddCounterEntity(ModelBuilder modelBuilder, string? schema)
    {
        var counter = modelBuilder.Entity<Counter>();

        counter.Property(p => p.Id);
        counter.HasKey(p => p.Id);
        counter.Property(p => p.Key);
        counter.Property(p => p.Value);

        counter.HasIndex(p => p.Key);

        counter.Metadata.SetSchema(schema);
    }

    private static void AddServerTaskEntity(ModelBuilder modelBuilder, string? schema)
    {
        var serverTask = modelBuilder.Entity<ServerTask>();

        serverTask.Property(p => p.Id);
        serverTask.HasKey(p => p.Id);
        serverTask.Property(p => p.ServerId);
        serverTask.Property(p => p.TaskName);
        serverTask.Property(p => p.IntervalSeconds);
        serverTask.Property(p => p.LastStatus);
        serverTask.Property(p => p.LastMessage);
        serverTask.Property(p => p.LastRun);
        serverTask.Property(p => p.LastDurationMs);

        serverTask.HasOne<Server>()
            .WithMany()
            .HasForeignKey(p => p.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        serverTask.HasIndex(p => p.ServerId);

        serverTask.Metadata.SetSchema(schema);
    }

    private static void AddServerLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var serverLog = modelBuilder.Entity<ServerLog>();

        serverLog.Property(p => p.Id);
        serverLog.HasKey(p => p.Id);
        serverLog.Property(p => p.ServerId);
        serverLog.Property(p => p.ServerTaskId);
        serverLog.Property(p => p.Status);
        serverLog.Property(p => p.Message);
        serverLog.Property(p => p.Timestamp);
        serverLog.Property(p => p.DurationMs);

        serverLog.HasOne<Server>()
            .WithMany()
            .HasForeignKey(p => p.ServerId)
            .OnDelete(DeleteBehavior.Cascade);

        serverLog.HasIndex(p => p.ServerId);
        serverLog.HasIndex(p => p.ServerTaskId);
        serverLog.HasIndex(p => p.Timestamp);

        serverLog.Metadata.SetSchema(schema);
    }

    public static void AddCircuitBreakerStateEntity(ModelBuilder modelBuilder, string? schema)
    {
        var state = modelBuilder.Entity<CircuitBreakerState>();

        state.Property(p => p.GroupKey).HasMaxLength(200).IsRequired();
        state.HasKey(p => p.GroupKey);

        state.Property(p => p.FailureCount);
        state.Property(p => p.OpenUntil);
        state.Property(p => p.LastFailureAt);
        state.Property(p => p.State).HasConversion<int>();

        state.HasIndex(p => p.OpenUntil);

        state.Metadata.SetSchema(schema);
    }

    public static void AddConcurrencyLimitEntity(ModelBuilder modelBuilder, string? schema)
    {
        var limit = modelBuilder.Entity<ConcurrencyLimit>();

        limit.Property(p => p.Name).HasMaxLength(200).IsRequired();
        limit.HasKey(p => p.Name);

        limit.Property(p => p.Limit);
        limit.Property(p => p.UpdatedAt);

        limit.Metadata.SetSchema(schema);
    }

    public static void AddRateLimitBucketEntity(ModelBuilder modelBuilder, string? schema)
    {
        var bucket = modelBuilder.Entity<RateLimitBucket>();

        bucket.Property(p => p.Name).HasMaxLength(200).IsRequired();
        bucket.HasKey(p => p.Name);

        bucket.Property(p => p.WindowStartUtc);
        bucket.Property(p => p.CurrentCount);
        bucket.Property(p => p.TimestampsJson);
        bucket.Property(p => p.UpdatedAt);

        bucket.Metadata.SetSchema(schema);
    }

    public static void AddRateLimitOverrideEntity(ModelBuilder modelBuilder, string? schema)
    {
        var ovr = modelBuilder.Entity<RateLimitOverride>();

        ovr.Property(p => p.Name).HasMaxLength(200).IsRequired();
        ovr.HasKey(p => p.Name);

        ovr.Property(p => p.Count);
        ovr.Property(p => p.WindowSeconds);
        ovr.Property(p => p.UpdatedAt);

        ovr.Metadata.SetSchema(schema);
    }

    public static void AddSagaJobLinkEntity(ModelBuilder modelBuilder, string? schema)
    {
        var link = modelBuilder.Entity<SagaJobLink>();

        link.HasKey(p => new { p.SagaId, p.JobId });
        link.Property(p => p.CreatedAt);

        // Activity-log ordering: range scan on SagaId + sort by CreatedAt.
        link.HasIndex(p => new { p.SagaId, p.CreatedAt });

        // Belt-and-braces FK to SagaState. The proxy/command-service path already removes links
        // alongside the saga via the change tracker, but a DB-level cascade catches direct DB
        // intervention or any future code path that doesn't go through the staged-RemoveRange
        // pattern.
        link.HasOne<SagaState>()
            .WithMany()
            .HasForeignKey(p => p.SagaId)
            .OnDelete(DeleteBehavior.Cascade);

        link.Metadata.SetSchema(schema);
    }

    public static void AddSagaStateEntity(ModelBuilder modelBuilder, string? schema)
    {
        var sagaState = modelBuilder.Entity<SagaState>();

        sagaState.Property(p => p.Id);
        sagaState.HasKey(p => p.Id);

        sagaState.Property(p => p.Type).HasMaxLength(400).IsRequired();
        sagaState.Property(p => p.CorrelationKey).HasMaxLength(200).IsRequired();
        sagaState.Property(p => p.StateJson).IsRequired();
        sagaState.Property(p => p.CreatedAt);
        sagaState.Property(p => p.UpdatedAt);

        sagaState.Property(p => p.Version).IsConcurrencyToken();

        // One live saga per (Type, CorrelationKey). Completion deletes the row, so re-use of
        // the correlation key after completion is immediately legal — same pattern as Wolverine.
        sagaState.HasIndex(p => new { p.Type, p.CorrelationKey }).IsUnique();

        // SagaQueryService.GetStats filters WHERE CreatedAt >= todayStart for the StartedToday
        // counter. Delete-on-completion bounds the table to live sagas only, but a deployment
        // with many long-lived sagas still benefits from an index lookup over a full scan.
        sagaState.HasIndex(p => p.CreatedAt);

        sagaState.Metadata.SetSchema(schema);
    }

    public static void AddBackgroundServiceDefinitionEntity(ModelBuilder modelBuilder, string? schema)
    {
        var def = modelBuilder.Entity<BackgroundServiceDefinition>();

        def.Property(p => p.Name).HasMaxLength(256).IsRequired();
        def.HasKey(p => p.Name);

        def.Property(p => p.DeclaredScope).HasConversion<int>();
        def.Property(p => p.FirstSeenAt);
        def.Property(p => p.LastSeenAt);

        def.Metadata.SetSchema(schema);
    }

    public static void AddBackgroundServiceInstanceEntity(ModelBuilder modelBuilder, string? schema)
    {
        var inst = modelBuilder.Entity<BackgroundServiceInstance>();

        inst.HasKey(p => new { p.ServerId, p.ServiceName });

        inst.Property(p => p.ServerId);
        inst.Property(p => p.ServiceName).HasMaxLength(256).IsRequired();
        inst.Property(p => p.DeclaredScope).HasConversion<int>();
        inst.Property(p => p.Status).HasConversion<int>();
        inst.Property(p => p.StartedAt);
        inst.Property(p => p.LastHeartbeatAt);
        inst.Property(p => p.LastError).HasMaxLength(4096);
        inst.Property(p => p.LastErrorAt);
        inst.Property(p => p.RestartCount);

        // FK → Definition. Restrict: must delete instance before definition.
        inst.HasOne<BackgroundServiceDefinition>()
            .WithMany()
            .HasForeignKey(p => p.ServiceName)
            .OnDelete(DeleteBehavior.Restrict);

        // FK → Server (nav property). Restrict: ServerCleanup and StopAsync explicitly
        // delete the Instance rows before the Server row, so cascade would be redundant.
        inst.HasOne(p => p.Server)
            .WithMany()
            .HasForeignKey(p => p.ServerId)
            .OnDelete(DeleteBehavior.Restrict);

        // ServerCleanup scans by ServerId to remove stale-server rows.
        inst.HasIndex(p => p.ServerId);

        inst.Metadata.SetSchema(schema);
    }

    public static void AddBackgroundServiceLeaseEntity(ModelBuilder modelBuilder, string? schema)
    {
        var lease = modelBuilder.Entity<BackgroundServiceLease>();

        lease.Property(p => p.ServiceName).HasMaxLength(256).IsRequired();
        lease.HasKey(p => p.ServiceName);

        lease.Property(p => p.HolderServerId);
        lease.Property(p => p.LeaseExpiresAt);

        // FK → Definition. Restrict: must delete lease before definition.
        lease.HasOne<BackgroundServiceDefinition>()
            .WithMany()
            .HasForeignKey(p => p.ServiceName)
            .OnDelete(DeleteBehavior.Restrict);

        // FK → Server via HolderServerId (nav: HolderServer). Restrict: same reasoning as Instance.
        lease.HasOne(p => p.HolderServer)
            .WithMany()
            .HasForeignKey(p => p.HolderServerId)
            .OnDelete(DeleteBehavior.Restrict);

        // ServerCleanup scans by HolderServerId to release leases held by dead servers.
        lease.HasIndex(p => p.HolderServerId);

        lease.Metadata.SetSchema(schema);
    }

    public static void AddAdapterDefinitionEntity(ModelBuilder modelBuilder, string? schema)
    {
        var def = modelBuilder.Entity<AdapterDefinition>();

        def.Property(p => p.Id);
        def.HasKey(p => p.Id);

        def.Property(p => p.Name).HasMaxLength(200).IsRequired();
        def.Property(p => p.FirstSeenAt);
        def.Property(p => p.LastSeenAt);
        def.Property(p => p.ConfigSummary).HasMaxLength(1024);
        def.Property(p => p.GroupLabel).HasMaxLength(64);
        def.Property(p => p.SharedPolicyJson);
        def.Property(p => p.SharedPolicyHash).HasMaxLength(128);
        def.Property(p => p.HasPolicyConflict);

        // Adapter name is the cluster-wide identity — one definition per name.
        def.HasIndex(p => p.Name).IsUnique();

        // ExpirationCleanup scans by LastSeenAt to remove orphaned definitions.
        def.HasIndex(p => p.LastSeenAt);

        def.Metadata.SetSchema(schema);
    }

    public static void AddAdapterCallLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<AdapterCallLog>();

        log.Property(p => p.Id);
        log.HasKey(p => p.Id);

        log.Property(p => p.AdapterName).HasMaxLength(200).IsRequired();
        log.Property(p => p.Operation).HasMaxLength(200).IsRequired();
        log.Property(p => p.GroupName).HasMaxLength(200);
        log.Property(p => p.Timestamp);
        log.Property(p => p.DurationMs);
        log.Property(p => p.Attempts);
        log.Property(p => p.Outcome).HasConversion<int>();
        log.Property(p => p.StatusCode);
        log.Property(p => p.ExceptionType).HasMaxLength(512);
        log.Property(p => p.ExceptionMessage).HasMaxLength(4096);
        log.Property(p => p.RequestSummary).HasMaxLength(2048);
        log.Property(p => p.RequestHeaders);
        log.Property(p => p.ResponseHeaders);
        log.Property(p => p.RequestBody);
        log.Property(p => p.ResponseBody);
        log.Property(p => p.MachineName).HasMaxLength(256).IsRequired();
        log.Property(p => p.TraceId).HasMaxLength(64);
        log.Property(p => p.TagsJson);
        log.Property(p => p.CorrelationId).HasMaxLength(200);
        log.Property(p => p.Application).HasMaxLength(200);
        log.Property(p => p.ExpireAt);

        // Per-adapter recent-calls listing.
        log.HasIndex(p => new { p.AdapterName, p.Timestamp });

        // Per-group recent-calls listing / stats.
        log.HasIndex(p => new { p.AdapterName, p.GroupName, p.Timestamp });

        // Domain-record lookup by correlation id (e.g. webhook delivery attempts).
        log.HasIndex(p => new { p.AdapterName, p.CorrelationId });

        // ExpirationCleanup range scan on expiry.
        log.HasIndex(p => p.ExpireAt);

        log.Metadata.SetSchema(schema);
    }

    public static void AddEndpointCallLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<EndpointCallLog>();

        log.Property(p => p.Id);
        log.HasKey(p => p.Id);

        log.Property(p => p.Method).HasMaxLength(16).IsRequired();
        log.Property(p => p.RouteTemplate).HasMaxLength(1024).IsRequired();
        log.Property(p => p.Operation).HasMaxLength(200).IsRequired();
        log.Property(p => p.GroupName).HasMaxLength(200);
        log.Property(p => p.Timestamp);
        log.Property(p => p.DurationMs);
        log.Property(p => p.Outcome).HasConversion<int>();
        log.Property(p => p.StatusCode);
        log.Property(p => p.RemoteIp).HasMaxLength(64);
        log.Property(p => p.Session).HasMaxLength(128);
        log.Property(p => p.UserAgent).HasMaxLength(1024);
        log.Property(p => p.User).HasMaxLength(256);
        log.Property(p => p.ExceptionType).HasMaxLength(512);
        log.Property(p => p.ExceptionMessage).HasMaxLength(4096);
        log.Property(p => p.RequestHeaders);
        log.Property(p => p.ResponseHeaders);
        log.Property(p => p.RequestBody);
        log.Property(p => p.ResponseBody);
        log.Property(p => p.MachineName).HasMaxLength(256).IsRequired();
        log.Property(p => p.TraceId);
        log.Property(p => p.TagsJson);
        log.Property(p => p.Application).HasMaxLength(200);
        log.Property(p => p.ExpireAt);

        // Per-endpoint recent-calls listing (identity = method + route template).
        log.HasIndex(p => new { p.Method, p.RouteTemplate, p.Timestamp });

        // Request→jobs drill-down joins jobs on the shared trace id.
        log.HasIndex(p => p.TraceId);

        // Session-timeline query (all server calls a browser session made).
        log.HasIndex(p => new { p.Session, p.Timestamp });

        // ExpirationCleanup range scan on expiry.
        log.HasIndex(p => p.ExpireAt);

        log.Metadata.SetSchema(schema);
    }

    public static void AddClientEventLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<ClientEventLog>();

        log.Property(p => p.Id);
        log.HasKey(p => p.Id);

        log.Property(p => p.Application).HasMaxLength(200);
        log.Property(p => p.Type).HasConversion<int>();
        log.Property(p => p.Name).HasMaxLength(512);
        log.Property(p => p.Level).HasMaxLength(32);
        log.Property(p => p.Message).HasMaxLength(4096);
        log.Property(p => p.Stack);
        log.Property(p => p.Value);
        log.Property(p => p.Url).HasMaxLength(2048);
        log.Property(p => p.TraceId);
        log.Property(p => p.SessionId).HasMaxLength(128);
        log.Property(p => p.Release).HasMaxLength(128);
        log.Property(p => p.UserAgent).HasMaxLength(1024);
        log.Property(p => p.RemoteIp).HasMaxLength(64);
        log.Property(p => p.Properties);
        log.Property(p => p.Breadcrumbs);
        log.Property(p => p.Timestamp);
        log.Property(p => p.ReceivedAt);
        log.Property(p => p.ExpireAt);

        // Per-application recent-events listing.
        log.HasIndex(p => new { p.Application, p.Timestamp });

        // Filter the event stream by kind.
        log.HasIndex(p => new { p.Type, p.Timestamp });

        // Session-timeline query (all events for one browser session, chronological).
        log.HasIndex(p => new { p.SessionId, p.Timestamp });

        // ExpirationCleanup range scan on expiry.
        log.HasIndex(p => p.ExpireAt);

        log.Metadata.SetSchema(schema);
    }

    public static void AddErrorGroupEntity(ModelBuilder modelBuilder, string? schema)
    {
        var group = modelBuilder.Entity<ErrorGroup>();

        group.Property(p => p.Id);
        group.HasKey(p => p.Id);

        group.Property(p => p.Fingerprint).HasMaxLength(64);
        group.Property(p => p.Source).HasConversion<int>();
        group.Property(p => p.Kind).HasConversion<int>();
        group.Property(p => p.ExceptionType).HasMaxLength(512);
        group.Property(p => p.Title).HasMaxLength(512);
        group.Property(p => p.Culprit).HasMaxLength(512);
        group.Property(p => p.StatusCode);
        group.Property(p => p.Application).HasMaxLength(200);
        group.Property(p => p.FirstSeenAt);
        group.Property(p => p.LastSeenAt);
        group.Property(p => p.Count);
        group.Property(p => p.LastSample);
        group.Property(p => p.SampleTraceId);
        group.Property(p => p.Status).HasConversion<int>();
        group.Property(p => p.StatusChangedAt);
        group.Property(p => p.ExpireAt);

        // One row per fingerprint — the upsert lookup key and the URL id.
        group.HasIndex(p => p.Fingerprint).IsUnique();

        // Issues list: filter by source/status, order by recency.
        group.HasIndex(p => new { p.Source, p.Status, p.LastSeenAt });

        // ExpirationCleanup range scan on expiry.
        group.HasIndex(p => p.ExpireAt);

        group.Metadata.SetSchema(schema);
    }

    public static void AddErrorOccurrenceEntity(ModelBuilder modelBuilder, string? schema)
    {
        var occurrence = modelBuilder.Entity<ErrorOccurrence>();

        occurrence.Property(p => p.Id);
        occurrence.HasKey(p => p.Id);

        occurrence.Property(p => p.Source).HasConversion<int>();
        occurrence.Property(p => p.Kind).HasConversion<int>();
        occurrence.Property(p => p.ExceptionType).HasMaxLength(512);
        occurrence.Property(p => p.Message).HasMaxLength(4096);
        occurrence.Property(p => p.Stack);
        occurrence.Property(p => p.Culprit).HasMaxLength(512);
        occurrence.Property(p => p.StatusCode);
        occurrence.Property(p => p.TraceId);
        occurrence.Property(p => p.Application).HasMaxLength(200);
        occurrence.Property(p => p.Timestamp);

        // The aggregator drains oldest-first; the orphan sweep ranges on the same column.
        occurrence.HasIndex(p => p.Timestamp);

        occurrence.Metadata.SetSchema(schema);
    }

    public static void AddWebhookDeliveryEntity(ModelBuilder modelBuilder, string? schema)
    {
        var delivery = modelBuilder.Entity<WebhookDelivery>();

        delivery.Property(p => p.Id);
        delivery.HasKey(p => p.Id);

        // Column caps mirror the SendAsync build choke point's clamp in the WebhookDispatcher (the single
        // place a WebhookDelivery row is built from caller input) so an over-long caller value clamps before
        // insert and never fails the row write. Only capped columns carry a length. HeadersJson, PayloadJson,
        // the converted RetrySchedule, and SuccessCodesJson hold unbounded content.
        delivery.Property(p => p.EventType).HasMaxLength(200).IsRequired();
        delivery.Property(p => p.EventId).HasMaxLength(200).IsRequired();
        delivery.Property(p => p.Url).HasMaxLength(2048).IsRequired();
        delivery.Property(p => p.HeadersJson);
        delivery.Property(p => p.GroupName).HasMaxLength(200);
        delivery.Property(p => p.Reference).HasMaxLength(200);
        delivery.Property(p => p.PayloadJson).IsRequired();
        delivery.Property(p => p.SigningMode).HasConversion<int>();
        delivery.Property(p => p.Secret).HasMaxLength(512);
        delivery.Property(p => p.RetrySchedule).HasConversion(RetryScheduleConverter.Converter, RetryScheduleConverter.Comparer);
        delivery.Property(p => p.SuccessCodesJson);
        delivery.Property(p => p.Status).HasConversion<int>();
        delivery.Property(p => p.AttemptCount);
        delivery.Property(p => p.ExhaustedCallbackPending);
        delivery.Property(p => p.NextAttemptAt);
        delivery.Property(p => p.CreatedAt);
        delivery.Property(p => p.Application).HasMaxLength(200);
        delivery.Property(p => p.ExpireAt);

        // Status filtering + display of the pending/next-attempt band.
        delivery.HasIndex(p => new { p.Status, p.NextAttemptAt });

        // Host lookup by its own subscription/definition reference.
        delivery.HasIndex(p => p.Reference);

        // Deliveries-by-event-type listing, newest first.
        delivery.HasIndex(p => new { p.EventType, p.CreatedAt });

        // ExpirationCleanup count-trim orders the settled set by CreatedAt globally (the composite
        // (EventType, CreatedAt) index can't serve a cross-event-type sort). A dedicated CreatedAt index
        // gives the sweep a pre-sorted scan — Pending rows are the residual predicate, not a sort key.
        delivery.HasIndex(p => p.CreatedAt);

        // ExpirationCleanup range scan on expiry.
        delivery.HasIndex(p => p.ExpireAt);

        delivery.Metadata.SetSchema(schema);
    }

    public static void AddApplicationInstanceEntity(ModelBuilder modelBuilder, string? schema)
    {
        var instance = modelBuilder.Entity<ApplicationInstance>();

        instance.Property(p => p.Id);
        instance.HasKey(p => p.Id);

        instance.Property(p => p.ApplicationName).HasMaxLength(200).IsRequired();
        instance.Property(p => p.MachineName).HasMaxLength(256).IsRequired();
        instance.Property(p => p.StartedAt);
        instance.Property(p => p.LastHeartbeatAt);
        instance.Property(p => p.CpuUsagePercent);
        instance.Property(p => p.MemoryWorkingSetBytes);
        instance.Property(p => p.Version).HasMaxLength(200);
        instance.Property(p => p.Environment).HasMaxLength(200);

        // Applications overview: instances grouped by application.
        instance.HasIndex(p => p.ApplicationName);

        // ExpirationCleanup stale-instance sweep by last heartbeat.
        instance.HasIndex(p => p.LastHeartbeatAt);

        instance.Metadata.SetSchema(schema);
    }

    public static void AddApplicationInstanceLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<ApplicationInstanceLog>();

        log.Property(p => p.Id);
        log.HasKey(p => p.Id);

        // InstanceId is a soft reference (Server.Id OR ApplicationInstance.Id) — no FK, like JobLog.
        log.Property(p => p.InstanceId);
        log.Property(p => p.ApplicationName).HasMaxLength(200).IsRequired();
        log.Property(p => p.Timestamp);
        log.Property(p => p.EventType).HasConversion<int>();
        log.Property(p => p.Message).HasMaxLength(4096);
        log.Property(p => p.ExpireAt);

        // Per-instance lifecycle timeline, newest first.
        log.HasIndex(p => new { p.InstanceId, p.Timestamp });

        // Per-application lifecycle timeline.
        log.HasIndex(p => new { p.ApplicationName, p.Timestamp });

        // ExpirationCleanup range scan on expiry.
        log.HasIndex(p => p.ExpireAt);

        log.Metadata.SetSchema(schema);
    }

    public static void AddBackgroundServiceLogEntity(ModelBuilder modelBuilder, string? schema)
    {
        var log = modelBuilder.Entity<BackgroundServiceLog>();

        log.Property(p => p.Id).ValueGeneratedOnAdd();
        log.HasKey(p => p.Id);

        log.Property(p => p.ServerId);
        log.Property(p => p.ServiceName).HasMaxLength(256).IsRequired();
        log.Property(p => p.Timestamp);
        log.Property(p => p.Level).HasConversion<int>();
        log.Property(p => p.Source).HasConversion<int>();
        log.Property(p => p.Message).HasMaxLength(4096).IsRequired();
        log.Property(p => p.ExceptionType).HasMaxLength(512);
        log.Property(p => p.ExceptionMessage).HasMaxLength(4096);

        // Cascade from Instance: when an instance row is removed, all its logs go with it.
        // This is the ONLY cascade in the background-services feature.
        log.HasOne<BackgroundServiceInstance>()
            .WithMany()
            .HasForeignKey(p => new { p.ServerId, p.ServiceName })
            .OnDelete(DeleteBehavior.Cascade);

        // FK → Server via nav property. Restrict (same reasoning as Instance/Lease) —
        // Logs are removed via the Instance cascade above before Server itself is deleted.
        log.HasOne(p => p.Server)
            .WithMany()
            .HasForeignKey(p => p.ServerId)
            .OnDelete(DeleteBehavior.Restrict);

        // Dashboard log-tail query: range scan for a specific instance, ordered newest-first.
        log.HasIndex(p => new { p.ServerId, p.ServiceName, p.Id });

        // Dashboard log-tail query filtered by service name only (cross-server view): range scan
        // on ServiceName with descending Id so the DB can return the N newest rows without a sort.
        log.HasIndex(p => new { p.ServiceName, p.Id })
            .IsDescending(false, true);

        log.Metadata.SetSchema(schema);
    }
}

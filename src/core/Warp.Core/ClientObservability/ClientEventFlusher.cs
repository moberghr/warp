using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Warp.Core.Data.Entities;
using Warp.Core.Enums;
using Warp.Core.ErrorGrouping;

namespace Warp.Core.ClientObservability;

/// <summary>
/// Drains the <see cref="DbClientEventRecorder"/> channel and persists client events in batches (§8.27) — the
/// client-side mirror of <c>EndpointCallFlusher{TContext}</c>. Each batch runs on a fresh DI scope (§0.5)
/// resolving the user's <typeparamref name="TContext"/> (client observability runs in any process); one
/// <c>SaveChanges</c> writes the <see cref="ClientEventLog"/> rows and the durable <see cref="Counter"/> fold.
/// The flusher stamps <c>ReceivedAt</c> + <c>ExpireAt</c> (from <c>WarpConfiguration.ClientEventLogRetention</c>)
/// and applies the <see cref="ClientEventCardinality"/> guard to the counter names (never the stored row). A
/// failed flush degrades to per-record persistence; the caller never observes recording failures.
/// </summary>
public sealed class ClientEventFlusher<TContext> : BackgroundService
    where TContext : DbContext
{
    private readonly DbClientEventRecorder _recorder;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClientEventCardinality _cardinality;
    private readonly WarpConfiguration _configuration;
    private readonly ILogger<ClientEventFlusher<TContext>> _logger;

    public ClientEventFlusher(
        DbClientEventRecorder recorder,
        IServiceScopeFactory scopeFactory,
        ClientEventCardinality cardinality,
        IOptions<WarpConfiguration> configuration,
        ILogger<ClientEventFlusher<TContext>> logger)
    {
        _recorder = recorder;
        _scopeFactory = scopeFactory;
        _cardinality = cardinality;
        _configuration = configuration.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _recorder.Reader;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var batch = new List<ClientEventRecord>();
            while (batch.Count < _configuration.CallLogFlushBatchSize && reader.TryRead(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            await FlushBatchAsync(batch, stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _recorder.Complete();
        await base.StopAsync(cancellationToken);
        await DrainRemainingAsync();
    }

    private Task DrainRemainingAsync()
        => DrainRemainingAsync(_recorder.Reader, FlushBatchAsync, ClientEventFlush.ShutdownDrainBudget, _configuration.CallLogFlushBatchSize);

    // Internal so tests can prove the drain budget bounds a hanging persist without a real database.
    internal static async Task DrainRemainingAsync(
        ChannelReader<ClientEventRecord> reader,
        Func<List<ClientEventRecord>, CancellationToken, Task> flush,
        TimeSpan budget,
        int batchSize = ClientEventFlush.BatchSize)
    {
        using var cts = new CancellationTokenSource(budget);

        while (!cts.IsCancellationRequested)
        {
            var batch = new List<ClientEventRecord>();
            while (batch.Count < batchSize && reader.TryRead(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count == 0)
            {
                break;
            }

            try
            {
                await flush(batch, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task FlushBatchAsync(List<ClientEventRecord> batch, CancellationToken ct)
    {
        var scopes = new List<IServiceScope>();
        try
        {
            await PersistWithFallbackAsync(CreateContext, batch, _cardinality, _configuration, _logger, ct);
        }
        finally
        {
            foreach (var scope in scopes)
            {
                scope.Dispose();
            }
        }

        (DbContext Context, TimeProvider TimeProvider) CreateContext()
        {
            var scope = _scopeFactory.CreateScope();
            scopes.Add(scope);

            return (
                scope.ServiceProvider.GetRequiredService<TContext>(),
                scope.ServiceProvider.GetRequiredService<TimeProvider>());
        }
    }

    internal static async Task PersistWithFallbackAsync(
        Func<(DbContext Context, TimeProvider TimeProvider)> contextFactory,
        List<ClientEventRecord> batch,
        ClientEventCardinality cardinality,
        WarpConfiguration configuration,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var (context, timeProvider) = contextFactory();
            await PersistBatchAsync(context, batch, cardinality, configuration, timeProvider, ct);

            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The shutdown drain budget elapsed mid-persist — the remaining tail is dropped (diagnostics, not
            // an audit trail). Log it so the data loss around a deploy is not silent (the one lossy path here
            // that would otherwise leave no trace).
            logger.LogWarning("Client-event flush cancelled at shutdown; dropped {Count} buffered record(s).", batch.Count);

            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Client-event batch flush failed for {Count} record(s); degrading to per-record persistence.", batch.Count);
        }

        foreach (var record in batch)
        {
            try
            {
                var (context, timeProvider) = contextFactory();
                await PersistBatchAsync(context, [record], cardinality, configuration, timeProvider, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Client-event record dropped ({Type}); sibling records persisted.", record.Type);
            }
        }
    }

    internal static async Task<int> PersistBatchAsync(
        DbContext context,
        IReadOnlyList<ClientEventRecord> batch,
        ClientEventCardinality cardinality,
        WarpConfiguration configuration,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (batch.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var expireAt = now.Add(configuration.ClientEventLogRetention);

        foreach (var record in batch)
        {
            context.Set<ClientEventLog>().Add(new ClientEventLog
            {
                Application = record.Application,
                Type = record.Type,
                Name = record.Name,
                Level = record.Level,
                Message = record.Message,
                Stack = record.Stack,
                Value = record.Value,
                Url = record.Url,
                TraceId = record.TraceId,
                SessionId = record.SessionId,
                Release = record.Release,
                UserAgent = record.UserAgent,
                RemoteIp = record.RemoteIp,
                Properties = record.Properties,
                Breadcrumbs = record.Breadcrumbs,
                Timestamp = record.Timestamp,
                ReceivedAt = now,
                ExpireAt = expireAt,
            });

            // The per-name aggregate dimension is the level for logs (§8.27 "logs count per level"), null for
            // requests (their value is the TraceId correlation, not an aggregate), the name otherwise; it is
            // cardinality-collapsed while the stored row above keeps the real value.
            var dimension = record.Type switch
            {
                ClientEventType.Log => record.Level,
                ClientEventType.Request => null,
                _ => record.Name,
            };
            var name = cardinality.Resolve(record.Type, dimension);
            foreach (var counter in ClientEventKeys.Build(record.Type, name, record.Value, record.Application, ClientEventKeys.HourBucket(record.Timestamp)))
            {
                context.Set<Counter>().Add(counter);
            }

            // Always-on meter for the name breakdown, tallied only after the cardinality guard has bounded the
            // name — the guard lives on this recording path, so (unlike the per-type/vital meters at ingest) an
            // Otel-only deployment reconstructs the top-N name breakdown only when recording is enabled. Emitting
            // the raw browser-sent name at the public ingest endpoint would be an unbounded-cardinality vector (§1.2).
            if (name is not null)
            {
                Logging.WarpTelemetry.RecordClientEventNamed(ClientEventKeys.TypeToken(record.Type), name, record.Application);
            }

            // Error-grouping inbox append (§8.29): a browser Error event is an error signal. Gated on the
            // grouping disable switch and folded into the same SaveChanges as the client-event row above.
            if (configuration.ErrorGroupingInterval is not null && record.Type == ClientEventType.Error)
            {
                context.Set<ErrorOccurrence>().Add(ErrorOccurrenceFactory.FromError(
                    ErrorSource.Client,
                    record.Name,
                    record.Message,
                    record.Stack,
                    record.Url ?? string.Empty,
                    record.TraceId,
                    record.Application,
                    record.Timestamp,
                    configuration.ApplicationVersion,
                    configuration.ApplicationEnvironment));
            }
        }

        await context.SaveChangesAsync(ct);

        return batch.Count;
    }
}

/// <summary>Non-generic tuning constants for the client-event flusher (kept off the generic type, S2743).</summary>
internal static class ClientEventFlush
{
    internal const int BatchSize = 500;

    internal static readonly TimeSpan ShutdownDrainBudget = TimeSpan.FromSeconds(5);
}

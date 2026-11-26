using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Adapters;
using Warp.Core.Data.Entities;
using Warp.Core.Entities;
using Warp.Core.Enums;
using Warp.Core.Webhooks;
using Warp.Tests.Fixtures;
using Warp.Worker;

namespace Warp.Tests.Webhooks;

/// <summary>
/// The limits on one webhook attempt (8.1): the response is sent with <c>ResponseHeadersRead</c> and only
/// the capture prefix of the body is read, the attempt is bounded by <c>WebhookAttemptTimeout</c>, and with
/// <c>PublicAddressesOnly</c> a non-public destination is refused. Every one of them is an ordinary failed
/// attempt on the delivery row and the executor job still completes.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookAttemptLimitsTestsBase : IntegrationTestBase
{
    protected WebhookAttemptLimitsTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Send_EndlessResponseBody_ReadsOnlyTheCapturePrefixAndDelivers()
    {
        // A body that never ends: buffering it (the pre-8.1 ResponseContentRead) never finishes. The attempt
        // must decide on the status, read at most the capture cap (8 KiB + 1 byte) and never drain the rest.
        var body = new EndlessBody();

        await using var server = await StartServerAsync(
            cfg => cfg.WebhookAttemptTimeout = TimeSpan.FromMinutes(5),
            new FixedResponseHandler(() => new StreamContent(body)));

        var deliveryId = await SendAsync(server, retrySchedule: []);

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        body.BytesServed.ShouldBeLessThanOrEqualTo(new WarpAdapterOptions().MaxCapturedBodySize + 1);

        var call = await WaitForCallAsync(server, deliveryId);
        call.Outcome.ShouldBe(AdapterCallOutcome.Success);
        call.ResponseBody.ShouldNotBeNullOrEmpty();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_SuccessBodyStalls_CaptureIsCutAtTheTimeoutAndTheDeliveryStaysDelivered()
    {
        // 200 headers arrive at once, the body never does. The capture read is inside the attempt budget, so
        // the attempt ends at WebhookAttemptTimeout instead of hanging; the outcome was already decided by
        // the status, so the delivery is Delivered with no captured body (a retry would resend the event).
        await using var server = await StartServerAsync(
            cfg => cfg.WebhookAttemptTimeout = TimeSpan.FromSeconds(1),
            new FixedResponseHandler(() => new StreamContent(new StalledBody())));

        var deliveryId = await SendAsync(server, retrySchedule: []);

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Delivered);

        var call = await WaitForCallAsync(server, deliveryId);
        call.Outcome.ShouldBe(AdapterCallOutcome.Success);
        call.ResponseBody.ShouldBeNull();

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_ResponseHeadersNeverArrive_TimeoutIsAFailedAttemptThatSchedulesTheRetry()
    {
        await using var server = await StartServerAsync(
            cfg => cfg.WebhookAttemptTimeout = TimeSpan.FromSeconds(1),
            new NeverRespondingHandler());

        var deliveryId = await SendAsync(server, retrySchedule: [TimeSpan.FromHours(1)]);

        // A timed-out attempt consumes a schedule slot like any failure: AttemptCount 1, still Pending, and
        // the retry is a Scheduled executor job.
        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<Job>()
                .Where(x => x.Queue == WebhookConstants.Queue)
                .Where(x => x.CurrentState == State.Scheduled)
                .AnyAsync(Ct),
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);

        var delivery = await GetDeliveryAsync(server, deliveryId);
        delivery.Status.ShouldBe(WebhookDeliveryStatus.Pending);
        delivery.AttemptCount.ShouldBe(1);

        var call = await WaitForCallAsync(server, deliveryId);
        call.ExceptionType.ShouldBe(typeof(TimeoutException).FullName);

        await AssertNoFailedJobsAsync(server);
    }

    [TimedFact]
    public async Task Send_PublicAddressesOnlyAndLoopbackUrl_RefusedAsFailedAttemptNamingTheHost()
    {
        // The real warp-webhooks client (no stubbed handler): the guard refuses loopback before any socket
        // opens, so nothing reaches the network.
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks(w => w.PublicAddressesOnly = true);
            });

        // SendAsync refuses a loopback literal up front when the guard is on (8.1), so the row is staged directly to
        // exercise the connect-time refusal (a host NAME resolving to loopback takes this path in production).
        var deliveryId = await StageDeliveryAsync(server, "http://127.0.0.1:9/hook");

        await WaitForDeliveryStatusAsync(server, deliveryId, WebhookDeliveryStatus.Exhausted);

        var call = await WaitForCallAsync(server, deliveryId);
        call.Outcome.ShouldBe(AdapterCallOutcome.Failed);
        call.ExceptionType.ShouldBe(typeof(WebhookAddressNotPublicException).FullName);
        call.ExceptionMessage.ShouldBe("Webhook host '127.0.0.1' does not resolve to a public address.");

        await AssertNoFailedJobsAsync(server);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static async Task<Guid> StageDeliveryAsync(WarpTestServer server, string url)
    {
        var deliveryId = Guid.NewGuid();
        var ctx = server.CreateContext();
        ctx.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            Id = deliveryId,
            EventType = "order.created",
            EventId = Guid.NewGuid().ToString(),
            Url = url,
            PayloadJson = "{}",
            SigningMode = WebhookSigning.None,
            RetrySchedule = [],
            Status = WebhookDeliveryStatus.Pending,
            NextAttemptAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync(Ct);

        var publisher = server.CreatePublisher();
        await publisher.Enqueue(new ExecuteWebhookDelivery { DeliveryId = deliveryId }, WebhookConstants.Queue);
        await publisher.SaveChangesAsync(Ct);

        return deliveryId;
    }

    private Task<WarpTestServer> StartServerAsync(Action<WarpServerConfiguration> configure, HttpMessageHandler primaryHandler)
    {
        return WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks();
                configure(cfg);
            },
            configureServices: services =>
                services.AddHttpClient(WebhookConstants.AdapterName)
                    .ConfigurePrimaryHttpMessageHandler(() => primaryHandler));
    }

    private static async Task<Guid> SendAsync(WarpTestServer server, IReadOnlyList<TimeSpan> retrySchedule, string url = "https://example.test/hook")
    {
        using var scope = server.GetService<IServiceScopeFactory>().CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>();

        return await dispatcher.SendAsync(
            new WebhookSend
            {
                Url = url,
                EventType = "order.created",
                Payload = "{\"order\":42}",
                RetrySchedule = retrySchedule,
            },
            Ct);
    }

    private static async Task<WebhookDelivery> GetDeliveryAsync(WarpTestServer server, Guid id)
    {
        return await server.CreateContext().Set<WebhookDelivery>()
            .AsNoTracking()
            .Where(x => x.Id == id)
            .FirstAsync(Ct);
    }

    private static Task WaitForDeliveryStatusAsync(WarpTestServer server, Guid id, WebhookDeliveryStatus status)
    {
        return WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == id)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == status,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);
    }

    private static async Task<AdapterCallLog> WaitForCallAsync(WarpTestServer server, Guid deliveryId)
    {
        var correlationId = deliveryId.ToString();

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == correlationId, Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        return await server.CreateContext().Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .FirstAsync(Ct);
    }

    private static async Task AssertNoFailedJobsAsync(WarpTestServer server)
    {
        var failed = await server.CreateContext().Set<Job>()
            .CountAsync(x => x.CurrentState == State.Failed, Ct);

        failed.ShouldBe(0);
    }
}

/// <summary>Returns 200 at once with the content the factory builds (one per request).</summary>
internal sealed class FixedResponseHandler : HttpMessageHandler
{
    private readonly Func<HttpContent> _content;

    public FixedResponseHandler(Func<HttpContent> content) => _content = content;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = _content() });
}

/// <summary>Never produces response headers; completes only when the attempt's token is cancelled.</summary>
internal sealed class NeverRespondingHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));

        return await never.Task;
    }
}

/// <summary>A response body that never ends; counts every byte handed out.</summary>
internal sealed class EndlessBody : Stream
{
    private long _served;

    public long BytesServed => Interlocked.Read(ref _served);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        buffer.AsSpan(offset, count).Fill((byte)'x');
        Interlocked.Add(ref _served, count);

        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        buffer.Span.Fill((byte)'x');
        Interlocked.Add(ref _served, buffer.Length);

        return ValueTask.FromResult(buffer.Length);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A response body whose first read never returns until the reader's token is cancelled.</summary>
internal sealed class StalledBody : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = cancellationToken.Register(() => never.TrySetCanceled(cancellationToken));

        return await never.Task;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

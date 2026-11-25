using System.Net;
using System.Net.Sockets;
using System.Text;
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

namespace Warp.Tests.Webhooks;

/// <summary>
/// The <c>warp-webhooks</c> client does not follow redirects (8.1): a 302 is the attempt's response, so under
/// the default success codes it is a failed attempt, and the redirect target never receives a request.
/// Runs the real default handler against a loopback server (no stubbed handler), on both databases.
/// </summary>
[GenerateDatabaseTests]
public abstract class WebhookRedirectTestsBase : IntegrationTestBase
{
    protected WebhookRedirectTestsBase(IDatabaseFixture fixture)
        : base(fixture)
    {
    }

    [TimedFact]
    public async Task Send_ReceiverAnswers302_RedirectIsNotFollowedAndTheAttemptFails()
    {
        await using var receiver = RedirectingReceiver.Start();
        await using var server = await WarpTestServer.StartAsync(
            Fixture,
            configure: cfg =>
            {
                cfg.Queues = ["default"];
                cfg.AddAdapters();
                cfg.AddWebhooks();
            });

        Guid deliveryId;
        using (var scope = server.GetService<IServiceScopeFactory>().CreateScope())
        {
            deliveryId = await scope.ServiceProvider.GetRequiredService<IWebhookDispatcher>().SendAsync(
                new WebhookSend
                {
                    Url = $"http://127.0.0.1:{receiver.Port}/hook",
                    EventType = "order.created",
                    Payload = "{\"order\":42}",
                    RetrySchedule = [],
                },
                Ct);
        }

        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<WebhookDelivery>()
                .Where(x => x.Id == deliveryId)
                .Select(x => (WebhookDeliveryStatus?)x.Status)
                .FirstOrDefaultAsync(Ct) == WebhookDeliveryStatus.Exhausted,
            timeout: TimeSpan.FromSeconds(8),
            ct: Ct);

        // The attempt (and any redirect it would have followed) finished before the outcome commit.
        receiver.RequestLines.ShouldBe(["POST /hook HTTP/1.1"]);

        var correlationId = deliveryId.ToString();
        await WarpTestServer.WaitUntil(
            async () => await server.CreateContext().Set<AdapterCallLog>()
                .AnyAsync(x => x.CorrelationId == correlationId, Ct),
            timeout: TimeSpan.FromSeconds(5),
            ct: Ct);

        var call = await server.CreateContext().Set<AdapterCallLog>()
            .AsNoTracking()
            .Where(x => x.CorrelationId == correlationId)
            .FirstAsync(Ct);

        call.StatusCode.ShouldBe(302);
        call.Outcome.ShouldBe(AdapterCallOutcome.Failed);

        (await server.CreateContext().Set<Job>().CountAsync(x => x.CurrentState == State.Failed, Ct)).ShouldBe(0);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;
}

/// <summary>
/// A minimal HTTP/1.1 server on a loopback port that answers every request with a 302 to <c>/moved</c> on
/// itself and records each request line, so a followed redirect would show up as a second request.
/// </summary>
internal sealed class RedirectingReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private readonly List<string> _requestLines = [];
    private readonly Task _acceptLoop;

    private RedirectingReceiver()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public IReadOnlyList<string> RequestLines
    {
        get
        {
            lock (_gate)
            {
                return [.. _requestLines];
            }
        }
    }

    public static RedirectingReceiver Start() => new();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();

        try
        {
            await _acceptLoop;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopping the listener ends the accept loop with one of these.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _ = ServeAsync(client);
        }
    }

    // Serves requests on one connection until the client closes it (keep-alive: a followed redirect to the
    // same origin would arrive on this connection).
    private async Task ServeAsync(TcpClient client)
    {
        using var connection = client;
        var stream = connection.GetStream();

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var head = await ReadHeadAsync(stream);
                if (head is null)
                {
                    return;
                }

                var lines = head.Split("\r\n");
                lock (_gate)
                {
                    _requestLines.Add(lines[0]);
                }

                await DrainBodyAsync(stream, ContentLength(lines));

                var response = $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{Port}/moved\r\nContent-Length: 0\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or the receiver is stopping.
        }
    }

    private async Task<string?> ReadHeadAsync(NetworkStream stream)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, _stop.Token) == 0)
            {
                return null;
            }

            head.Append((char)one[0]);
        }

        return head.ToString(0, head.Length - 4);
    }

    private async Task DrainBodyAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[Math.Max(length, 1)];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), _stop.Token);
            if (n == 0)
            {
                return;
            }

            read += n;
        }
    }

    private static int ContentLength(string[] headerLines)
    {
        var header = headerLines.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));

        return header is null ? 0 : int.Parse(header["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

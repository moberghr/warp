using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core.Handlers;
using Warp.Http;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

/// <summary>
/// Bombards the in-memory test app with hundreds of parallel requests to surface any
/// scope leakage, static-state crossover, AsyncLocal bleed, or missed-construction bugs
/// in the generated dispatch path. If any of these tests fail, isolation between requests
/// is broken — never silently flaky in CI; assertions are deterministic given a healthy
/// DI container.
/// </summary>
[Trait("Category", "NoDb")]
public sealed class ConcurrencyIsolationTests
{
    private const int RequestCount = 200;
    private const int StreamRequestCount = 50;

    [TimedFact]
    public async Task ParallelRequests_EachReceiveItsOwnRequestPayload()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        var markers = Enumerable.Range(0, RequestCount).Select(i => $"m-{i}").ToArray();

        var responses = new ConcurrentBag<(string Sent, string Received)>();
        await Parallel.ForEachAsync(markers, async (marker, ct) =>
        {
            var resp = await app.Client.GetAsync(new Uri($"/api/concurrency/echo?Marker={marker}", UriKind.Relative), ct);
            var body = await resp.Content.ReadFromJsonAsync<ConcurrencyEchoResponse>(ct);
            responses.Add((marker, body!.Marker));
        });

        responses.Count.ShouldBe(RequestCount);
        foreach (var pair in responses)
        {
            pair.Received.ShouldBe(pair.Sent);
        }
    }

    [TimedFact]
    public async Task ParallelRequests_EachGetTheirOwnDIScope()
    {
        var counter = new ScopeConstructionCounter();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s =>
            {
                s.AddSingleton(counter);
                s.AddScoped<ScopeProbe>();
            },
            configureApp: a => a.MapWarpHttp());

        var responses = new ConcurrentBag<Guid>();
        await Parallel.ForEachAsync(Enumerable.Range(0, RequestCount), async (i, ct) =>
        {
            var resp = await app.Client.GetAsync(new Uri($"/api/concurrency/scope?Marker=m-{i}", UriKind.Relative), ct);
            var body = await resp.Content.ReadFromJsonAsync<ConcurrencyScopeResponse>(ct);
            responses.Add(body!.ScopeId);
        });

        responses.Count.ShouldBe(RequestCount);

        // N requests must produce N unique scope-ids (one ScopeProbe constructed per request).
        responses.ToHashSet().Count.ShouldBe(RequestCount);
        counter.Count.ShouldBe(RequestCount);
    }

    [TimedFact]
    public async Task ParallelRequests_DoNotShareAsyncLocalState()
    {
        // AsyncLocalProbe.Value is a static AsyncLocal<Guid?>. Each handler asserts the
        // value is null on entry, then sets it. If the execution-context isolation is broken
        // (e.g. a request reusing another's logical call context), some handler will see
        // a non-null on entry and the probe records a leak.
        var probe = new AsyncLocalLeakRecorder();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s => s.AddSingleton(probe),
            configureApp: a => a.MapWarpHttp());

        await Parallel.ForEachAsync(Enumerable.Range(0, RequestCount), async (i, ct) =>
        {
            var resp = await app.Client.GetAsync(new Uri($"/api/concurrency/ambient?Marker=m-{i}", UriKind.Relative), ct);
            resp.EnsureSuccessStatusCode();
        });

        probe.Leaks.ShouldBeEmpty(
            "Some request observed a non-null AsyncLocal<Guid?> on handler entry — execution context is leaking between requests.");
    }

    [TimedFact]
    public async Task ParallelRequests_PipelineBehaviorSeesPerRequestInputs()
    {
        // The pipeline behavior records (request-marker, current scope-id) for every invocation.
        // After bombardment, every recorded scope-id must be unique (per-request scope) and
        // every recorded marker must match a marker we sent (no crossover).
        var sink = new BehaviorInvocationSink();
        await using var app = await WarpHttpTestApp.StartAsync(
            configureServices: s =>
            {
                s.AddSingleton(sink);
                s.AddScoped<ScopeProbe>();
                s.AddSingleton(new ScopeConstructionCounter());
                s.AddTransient<IPipelineBehavior<ConcurrencyEcho, ConcurrencyEchoResponse>, RecordingBehavior>();
            },
            configureApp: a => a.MapWarpHttp());

        var markers = Enumerable.Range(0, RequestCount).Select(i => $"b-{i}").ToHashSet();

        await Parallel.ForEachAsync(markers, async (marker, ct) =>
        {
            var resp = await app.Client.GetAsync(new Uri($"/api/concurrency/echo?Marker={marker}", UriKind.Relative), ct);
            resp.EnsureSuccessStatusCode();
        });

        sink.Invocations.Count.ShouldBe(RequestCount);
        sink.Invocations.Select(x => x.Marker).ToHashSet().Count.ShouldBe(RequestCount);
        sink.Invocations.Select(x => x.ScopeId).ToHashSet().Count.ShouldBe(RequestCount);
        foreach (var inv in sink.Invocations)
        {
            markers.ShouldContain(inv.Marker);
        }
    }

    [TimedFact]
    public async Task ParallelStreams_DoNotCrossoverItems()
    {
        await using var app = await WarpHttpTestApp.StartAsync(configureApp: a => a.MapWarpHttp());

        // Each stream emits a contiguous block: Start..Start+Count-1. Block ranges are
        // disjoint by construction — if any client receives an item outside its own block,
        // streams crossed over.
        const int blockSize = 25;
        var clients = Enumerable.Range(0, StreamRequestCount)
            .Select(i => (Index: i, Start: i * blockSize, Count: blockSize))
            .ToArray();

        var perClient = new ConcurrentDictionary<int, List<int>>();
        await Parallel.ForEachAsync(clients, async (c, ct) =>
        {
            var url = new Uri($"/api/concurrency/stream?Start={c.Start}&Count={c.Count}", UriKind.Relative);
            using var resp = await app.Client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            var values = body
                .Split('\n')
                .Where(line => line.StartsWith("data: ", StringComparison.Ordinal))
                .Select(line => int.Parse(line["data: ".Length..], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();

            perClient[c.Index] = values;
        });

        perClient.Count.ShouldBe(StreamRequestCount);
        foreach (var c in clients)
        {
            var got = perClient[c.Index];
            var expected = Enumerable.Range(c.Start, c.Count).ToList();
            got.ShouldBe(expected, $"client {c.Index} (range {c.Start}..{c.Start + c.Count - 1}) saw {string.Join(',', got)}");
        }
    }

    public sealed class ScopeConstructionCounter
    {
        private int _count;

        public int Count => _count;

        public void Increment() => Interlocked.Increment(ref _count);
    }

    public sealed class ScopeProbe
    {
        public ScopeProbe(ScopeConstructionCounter counter)
        {
            Counter = counter;
            Counter.Increment();
        }

        public Guid Id { get; } = Guid.NewGuid();

        public ScopeConstructionCounter Counter { get; }
    }

    public sealed class AsyncLocalLeakRecorder
    {
        private static readonly AsyncLocal<Guid?> AmbientValue = new();

        private readonly ConcurrentBag<string> _leaks = [];

        public IReadOnlyCollection<string> Leaks => _leaks;

        public void Observe(string marker)
        {
            if (AmbientValue.Value is { } existing)
            {
                _leaks.Add($"marker={marker} saw existing ambient={existing}");
            }

            AmbientValue.Value = Guid.NewGuid();
        }
    }

    public sealed class BehaviorInvocationSink
    {
        private readonly ConcurrentBag<(string Marker, Guid ScopeId)> _invocations = [];

        public IReadOnlyCollection<(string Marker, Guid ScopeId)> Invocations => _invocations;

        public void Record(string marker, Guid scopeId) => _invocations.Add((marker, scopeId));
    }

    public sealed class RecordingBehavior : IPipelineBehavior<ConcurrencyEcho, ConcurrencyEchoResponse>
    {
        private readonly BehaviorInvocationSink _sink;
        private readonly ScopeProbe _probe;

        public RecordingBehavior(BehaviorInvocationSink sink, ScopeProbe probe)
        {
            _sink = sink;
            _probe = probe;
        }

        public Task<ConcurrencyEchoResponse> HandleAsync(
            ConcurrencyEcho request,
            RequestHandlerDelegate<ConcurrencyEcho, ConcurrencyEchoResponse> next,
            CancellationToken cancellationToken)
        {
            _sink.Record(request.Marker, _probe.Id);
            return next(request, cancellationToken);
        }
    }
}

public sealed record ConcurrencyEcho([FromQuery] string Marker) : IRequest<ConcurrencyEchoResponse>;

public sealed record ConcurrencyEchoResponse(string Marker);

[WarpHttpGet("/api/concurrency/echo")]
public sealed class ConcurrencyEchoHandler : IRequestHandler<ConcurrencyEcho, ConcurrencyEchoResponse>
{
    public Task<ConcurrencyEchoResponse> HandleAsync(ConcurrencyEcho request, CancellationToken cancellationToken)
        => Task.FromResult(new ConcurrencyEchoResponse(request.Marker));
}

public sealed record ConcurrencyScopeQuery([FromQuery] string Marker) : IRequest<ConcurrencyScopeResponse>;

public sealed record ConcurrencyScopeResponse(Guid ScopeId, string Marker);

[WarpHttpGet("/api/concurrency/scope")]
public sealed class ConcurrencyScopeHandler : IRequestHandler<ConcurrencyScopeQuery, ConcurrencyScopeResponse>
{
    private readonly ConcurrencyIsolationTests.ScopeProbe _probe;

    public ConcurrencyScopeHandler(ConcurrencyIsolationTests.ScopeProbe probe)
    {
        _probe = probe;
    }

    public Task<ConcurrencyScopeResponse> HandleAsync(ConcurrencyScopeQuery request, CancellationToken cancellationToken)
        => Task.FromResult(new ConcurrencyScopeResponse(_probe.Id, request.Marker));
}

public sealed record ConcurrencyAmbient([FromQuery] string Marker) : IRequest<Unit>;

[WarpHttpGet("/api/concurrency/ambient")]
public sealed class ConcurrencyAmbientHandler : IRequestHandler<ConcurrencyAmbient, Unit>
{
    private readonly ConcurrencyIsolationTests.AsyncLocalLeakRecorder _recorder;

    public ConcurrencyAmbientHandler(ConcurrencyIsolationTests.AsyncLocalLeakRecorder recorder)
    {
        _recorder = recorder;
    }

    public Task<Unit> HandleAsync(ConcurrencyAmbient request, CancellationToken cancellationToken)
    {
        _recorder.Observe(request.Marker);
        return Task.FromResult(Unit.Value);
    }
}

public sealed record ConcurrencyStreamQuery([FromQuery] int Start, [FromQuery] int Count) : IStreamRequest<int>;

[WarpHttpGet("/api/concurrency/stream")]
public sealed class ConcurrencyStreamHandler : IStreamRequestHandler<ConcurrencyStreamQuery, int>
{
    public async IAsyncEnumerable<int> HandleAsync(ConcurrencyStreamQuery request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 0; i < request.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await Task.FromResult(request.Start + i).ConfigureAwait(false);
        }
    }
}

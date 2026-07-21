using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Warp.Adapters.Http;
using Warp.Tests.TestData.Handlers;

namespace Warp.Tests.Adapters;

/// <summary>
/// NoDb coverage for the HTTP operation-name resolver: precedence (SC2), URL heuristic with
/// numeric/GUID collapse, the <c>MaxDistinctOperations</c> cardinality guard (SC12), and absolute-URI
/// (no <c>BaseUrl</c>) resolution (SC14).
/// </summary>
[Trait("Category", "NoDb")]
public class OperationNameResolverTests
{
    [TimedFact]
    public void RequestOption_WinsOverAmbientAndHeuristic()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/orders/123");
        request.WithWarpOperation("ExplicitOp");

        using (WarpAdapterCall.Operation("AmbientOp"))
        {
            resolver.Resolve("vendor", request, 50).ShouldBe("ExplicitOp");
        }
    }

    [TimedFact]
    public void AmbientScope_WinsOverHeuristic()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/orders/123");

        using (WarpAdapterCall.Operation("AmbientOp"))
        {
            resolver.Resolve("vendor", request, 50).ShouldBe("AmbientOp");
        }
    }

    [TimedFact]
    public void Heuristic_CollapsesNumericSegment_ToIdPlaceholder()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/orders/123/items");

        resolver.Resolve("vendor", request, 50).ShouldBe("GET /orders/{id}/items");
    }

    [TimedFact]
    public void Heuristic_CollapsesGuidSegment_ToIdPlaceholder()
    {
        var resolver = CreateResolver();
        var guid = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.vendor.com/customers/{guid}");

        resolver.Resolve("vendor", request, 50).ShouldBe("POST /customers/{id}");
    }

    [TimedFact]
    public void Heuristic_PlainPath_Unchanged()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/orders");

        resolver.Resolve("vendor", request, 50).ShouldBe("GET /orders");
    }

    [TimedFact]
    public void Heuristic_AbsoluteUri_NoBaseUrl_ResolvesViaPath()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "https://per-tenant-host.example.com/v2/sessions/42");

        resolver.Resolve("webhooks", request, 50).ShouldBe("DELETE /v2/sessions/{id}");
    }

    [TimedFact]
    public void Heuristic_RelativeUri_CollapsesIdSegments()
    {
        // The mainline shape whenever BaseUrl is configured: the request carries a RELATIVE path, which
        // takes the manual OriginalString branch (Uri.AbsolutePath throws on relative URIs).
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("orders/42/items", UriKind.Relative));

        resolver.Resolve("rel-vendor", request, 50).ShouldBe("GET /orders/{id}/items");
    }

    [TimedFact]
    public void Heuristic_RelativeUri_StripsQueryString()
    {
        // The relative branch strips the query manually (IndexOf('?')) — a regression here would explode
        // operation cardinality with one name per query-string permutation.
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("orders?page=2&size=50", UriKind.Relative));

        resolver.Resolve("rel-vendor", request, 50).ShouldBe("GET /orders");
    }

    [TimedFact]
    public void Heuristic_RelativeUri_TrailingSlash_NormalisesLikeAbsolute()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("orders/123/", UriKind.Relative));

        resolver.Resolve("rel-vendor", request, 50).ShouldBe("GET /orders/{id}");
    }

    [TimedFact]
    public void Heuristic_AbsoluteUri_EmptyPath_ResolvesToRoot()
    {
        var resolver = CreateResolver();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com");

        resolver.Resolve("root-vendor", request, 50).ShouldBe("GET /");
    }

    [TimedFact]
    public void Cardinality_HeuristicBeyondMax_CollapsesToOther()
    {
        var resolver = CreateResolver();

        Resolve(resolver, "/a").ShouldBe("GET /a");
        Resolve(resolver, "/b").ShouldBe("GET /b");
        Resolve(resolver, "/c").ShouldBe("{other}");
    }

    [TimedFact]
    public void Cardinality_ExplicitNames_NeverCollapse()
    {
        var resolver = CreateResolver();

        // Fill the cap of 1 with a heuristic name on the "explicit-adapter".
        using var first = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/a");
        resolver.Resolve("explicit-adapter", first, 1).ShouldBe("GET /a");

        // A further heuristic name would collapse, but an explicit name bypasses the guard entirely.
        using var beyond = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/b");
        beyond.WithWarpOperation("ExplicitBeyondCap");

        resolver.Resolve("explicit-adapter", beyond, 1).ShouldBe("ExplicitBeyondCap");
    }

    [TimedFact]
    public void Cardinality_CollapseWarnsExactlyOnce_AcrossRepeatedCollapses()
    {
        // The one-time warning fires on the FIRST collapse and never again for that adapter, no matter how
        // many further new heuristic names collapse — a per-adapter warn-once, not per-collapse spam.
        var logger = new CapturingLogger<OperationNameResolver>();
        var resolver = new OperationNameResolver(logger);

        ResolveCapped(resolver, "/a").ShouldBe("GET /a"); // fills the cap of 1
        ResolveCapped(resolver, "/b").ShouldBe("{other}"); // first collapse → warns
        ResolveCapped(resolver, "/c").ShouldBe("{other}"); // further collapses → no additional warning
        ResolveCapped(resolver, "/d").ShouldBe("{other}");

        logger.WarningCount.ShouldBe(1);
    }

    private static string ResolveCapped(OperationNameResolver resolver, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com" + path);

        return resolver.Resolve("warn-once", request, 1);
    }

    [TimedFact]
    public void Cardinality_RepeatedHeuristic_DoesNotConsumeCapTwice()
    {
        var resolver = CreateResolver();

        Resolve(resolver, "/a").ShouldBe("GET /a");
        Resolve(resolver, "/a").ShouldBe("GET /a");
        Resolve(resolver, "/b").ShouldBe("GET /b");
    }

    [TimedFact]
    public async Task Cardinality_ConcurrentNewHeuristicsAtCap_AdmitsExactlyOne()
    {
        // Cap heuristic operations at 2 and consume one, leaving a single slot. Two concurrent NEW heuristic
        // paths race the count-then-add: the atomic bound admits exactly one real name; the other collapses
        // to {other}. Without the lock both could clear the count check and overshoot the cap.
        var resolver = CreateResolver();
        using (var seed = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com/seed"))
        {
            resolver.Resolve("race", seed, 2);
        }

        var barrier = new BarrierSignal();
        var one = ResolveUnderBarrier(resolver, "/a", barrier);
        var two = ResolveUnderBarrier(resolver, "/b", barrier);

        await barrier.Running.WaitAsync(Ct);
        await barrier.Running.WaitAsync(Ct);
        barrier.CanFinish.Release(2);

        var results = await Task.WhenAll(one, two);

        results.Count(x => string.Equals(x, "{other}", StringComparison.Ordinal)).ShouldBe(1);
        results.Count(x => !string.Equals(x, "{other}", StringComparison.Ordinal)).ShouldBe(1);
    }

    private static Task<string> ResolveUnderBarrier(OperationNameResolver resolver, string path, BarrierSignal barrier)
        => Task.Run(async () =>
        {
            barrier.Running.Release();
            await barrier.CanFinish.WaitAsync(Ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com" + path);

            return resolver.Resolve("race", request, 2);
        });

    private static string Resolve(OperationNameResolver resolver, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.vendor.com" + path);

        return resolver.Resolve("capped", request, 2);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static OperationNameResolver CreateResolver() => new(NullLogger<OperationNameResolver>.Instance);
}

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that counts emitted <see cref="LogLevel.Warning"/> entries — lets the
/// warn-once cardinality tests assert the one-time warning fires exactly once per adapter.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    private int _warningCount;

    public int WarningCount => _warningCount;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning)
        {
            Interlocked.Increment(ref _warningCount);
        }
    }
}

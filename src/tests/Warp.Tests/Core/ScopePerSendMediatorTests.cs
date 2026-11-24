using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Http.Dispatch;

namespace Warp.Tests.Core;

// Issue #321: with Mediator.ScopePerSend each Send / CreateStream runs in its own DI scope. In Blazor
// Server the ambient scope is the circuit, so without it every send from a page shares one scoped
// DbContext. A scoped Probe stands in for that DbContext: same instance = same scope.
[Trait("Category", "NoDb")]
public class ScopePerSendMediatorTests
{
    [TimedFact]
    public async Task Send_TwoSends_RunInDifferentScopesAndNotTheCallers()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var first = await mediator.Send(new ProbeRequest(), ct);
        var second = await mediator.Send(new ProbeRequest(), ct);

        first.ProbeId.ShouldNotBe(second.ProbeId);
        first.ProbeId.ShouldNotBe(outer.ServiceProvider.GetRequiredService<Probe>().Id);
    }

    [TimedFact]
    public async Task Send_WhenItReturns_TheScopeIsDisposed()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);

        var result = await mediator.Send(new ProbeRequest(), Xunit.TestContext.Current.CancellationToken);

        result.Probe.Disposed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task Send_WithInitializer_CopiesStateFromTheCallersScope()
    {
        await using var provider = BuildProvider(x => x.AddSingleton<ISendScopeInitializer, TenantInitializer>());
        await using var outer = provider.CreateAsyncScope();
        outer.ServiceProvider.GetRequiredService<Probe>().Tenant = "acme";
        var mediator = CreateMediator(outer.ServiceProvider);

        var result = await mediator.Send(new ProbeRequest(), Xunit.TestContext.Current.CancellationToken);

        result.Tenant.ShouldBe("acme");
    }

    [TimedFact]
    public async Task Send_NestedSendFromAHandler_GetsItsOwnScope()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);

        var result = await mediator.Send(new NestedRequest(), Xunit.TestContext.Current.CancellationToken);

        result.OuterProbeId.ShouldNotBe(result.InnerProbeId);
    }

    [TimedFact]
    public async Task Send_OfAStreamRequest_RunsInTheCallersScopeSoTheStreamOutlivesTheCall()
    {
        // Send returns the enumerable before anything is enumerated: a scope of its own would already be
        // disposed when the caller starts reading.
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);
        var ct = Xunit.TestContext.Current.CancellationToken;

        var stream = await mediator.Send(new ProbeStreamRequest(), ct);

        await foreach (var item in stream.WithCancellation(ct))
        {
            item.Probe.Disposed.ShouldBeFalse();
            item.ProbeId.ShouldBe(outer.ServiceProvider.GetRequiredService<Probe>().Id);
        }
    }

    [TimedFact]
    public async Task CreateStream_NeverEnumerated_CreatesNoScope()
    {
        var created = new ProbeCounter();
        await using var provider = BuildProvider(x => x.AddSingleton(created));
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);

        _ = mediator.CreateStream(new ProbeStreamRequest(), Xunit.TestContext.Current.CancellationToken);

        created.Count.ShouldBe(0);
    }

    [TimedFact]
    public async Task CreateStream_Enumerated_UsesOneScopeAndDisposesItAtTheEnd()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);
        var items = new List<ProbeResult>();

        await foreach (var item in mediator.CreateStream(new ProbeStreamRequest(), Xunit.TestContext.Current.CancellationToken))
        {
            item.Probe.Disposed.ShouldBeFalse();
            items.Add(item);
        }

        items.Count.ShouldBe(2);
        items[0].ProbeId.ShouldBe(items[1].ProbeId);
        items[0].Probe.Disposed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task CreateStream_AbandonedMidWay_DisposesTheScope()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);
        Probe probe;

        // Take the first item and dispose the enumerator, as an abandoned await foreach does.
        await using (var enumerator = mediator.CreateStream(new ProbeStreamRequest(), Xunit.TestContext.Current.CancellationToken).GetAsyncEnumerator(Xunit.TestContext.Current.CancellationToken))
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();
            probe = enumerator.Current.Probe;
            probe.Disposed.ShouldBeFalse();
        }

        probe.Disposed.ShouldBeTrue();
    }

    [TimedFact]
    public async Task InCurrentScope_Send_RunsInTheCallersScope()
    {
        await using var provider = BuildProvider();
        await using var outer = provider.CreateAsyncScope();
        var mediator = CreateMediator(outer.ServiceProvider);

        var result = await mediator.InCurrentScope.Send(new ProbeRequest(), Xunit.TestContext.Current.CancellationToken);

        result.ProbeId.ShouldBe(outer.ServiceProvider.GetRequiredService<Probe>().Id);
    }

    // Warp's HTTP binding already has one scope per request, so an endpoint's handler keeps running in
    // the request's scope (sharing its DbContext) even with the option on.
    [TimedFact]
    public async Task HttpInvocation_WithScopePerSend_RunsTheHandlerInTheRequestScope()
    {
        await using var provider = BuildProvider(x => x.ConfigureHttpJsonOptions(_ => { }));
        await using var requestScope = provider.CreateAsyncScope();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
        context.Response.Body = body;

        await WarpHttpInvocation.InvokeRequest<ProbeRequest, ProbeResult>(context, new ProbeRequest());

        var json = System.Text.Encoding.UTF8.GetString(body.ToArray());
        json.ShouldContain(requestScope.ServiceProvider.GetRequiredService<Probe>().Id.ToString());
    }

    [TimedTheory]
    [InlineData(false, typeof(Mediator))]
    [InlineData(true, typeof(ScopePerSendMediator))]
    public async Task AddWarp_MediatorScopePerSend_SelectsTheMediator(bool scopePerSend, Type expected)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"scope-per-send-{Guid.NewGuid():N}"));
        services.AddWarp<TestContext>(opt => opt.Mediator.ScopePerSend = scopePerSend);
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IMediator>().ShouldBeOfType(expected);
    }

    private static ScopePerSendMediator CreateMediator(IServiceProvider scope) =>
        new(scope, scope.GetRequiredService<IServiceScopeFactory>(), scope.GetServices<ISendScopeInitializer>());

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ProbeCounter>();
        services.AddScoped<Probe>();
        services.AddTransient<IRequestHandler<ProbeRequest, ProbeResult>, ProbeHandler>();
        services.AddTransient<IRequestHandler<NestedRequest, NestedResult>, NestedHandler>();
        services.AddTransient<IStreamRequestHandler<ProbeStreamRequest, ProbeResult>, ProbeStreamHandler>();

        // What AddWarp registers when the option is on, so a handler's own IMediator scopes its sends too.
        services.AddScoped<IMediator>(x => CreateMediator(x));
        configure?.Invoke(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed class ProbeCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);
    }

    public sealed class Probe : IDisposable
    {
        public Probe(ProbeCounter counter) => counter.Increment();

        public Guid Id { get; } = Guid.NewGuid();

        public string? Tenant { get; set; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public sealed record ProbeResult(Guid ProbeId, string? Tenant, Probe Probe);

    public sealed record NestedResult(Guid OuterProbeId, Guid InnerProbeId);

    public sealed class ProbeRequest : IRequest<ProbeResult>;

    public sealed class NestedRequest : IRequest<NestedResult>;

    public sealed class ProbeStreamRequest : IStreamRequest<ProbeResult>;

    // These handlers take the provider and resolve when they run, not in the constructor: the source
    // generator registers every handler of this assembly in the shared GeneratedMediator, which resolves
    // them all at construction, in test hosts that do not register Probe.
    public sealed class ProbeHandler(IServiceProvider services) : IRequestHandler<ProbeRequest, ProbeResult>
    {
        public Task<ProbeResult> HandleAsync(ProbeRequest request, CancellationToken cancellationToken)
        {
            var probe = services.GetRequiredService<Probe>();

            return Task.FromResult(new ProbeResult(probe.Id, probe.Tenant, probe));
        }
    }

    public sealed class NestedHandler(IServiceProvider services) : IRequestHandler<NestedRequest, NestedResult>
    {
        public async Task<NestedResult> HandleAsync(NestedRequest request, CancellationToken cancellationToken)
        {
            var inner = await services.GetRequiredService<IMediator>().Send(new ProbeRequest(), cancellationToken);

            return new NestedResult(services.GetRequiredService<Probe>().Id, inner.ProbeId);
        }
    }

    public sealed class ProbeStreamHandler(IServiceProvider services) : IStreamRequestHandler<ProbeStreamRequest, ProbeResult>
    {
        public async IAsyncEnumerable<ProbeResult> HandleAsync(ProbeStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var probe = services.GetRequiredService<Probe>();

            yield return new ProbeResult(probe.Id, probe.Tenant, probe);
            await Task.Yield();
            yield return new ProbeResult(probe.Id, probe.Tenant, probe);
        }
    }

    public sealed class TenantInitializer : ISendScopeInitializer
    {
        public void Initialize(IServiceProvider parent, IServiceProvider child) =>
            child.GetRequiredService<Probe>().Tenant = parent.GetRequiredService<Probe>().Tenant;
    }
}

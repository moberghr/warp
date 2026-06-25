using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Warp.Http;

namespace Warp.Tests.Http;

/// <summary>
/// Minimal in-memory ASP.NET host for Warp.Http tests. Each test composes services and
/// pipeline via the <paramref name="configureServices"/> / <paramref name="configureApp"/>
/// callbacks, then asserts against the returned <see cref="HttpClient"/>. Implements
/// <see cref="IAsyncDisposable"/> so tests can <c>await using</c> it.
/// </summary>
public sealed class WarpHttpTestApp : IAsyncDisposable
{
    private readonly IHost _host;

    private WarpHttpTestApp(IHost host, HttpClient client)
    {
        _host = host;
        Client = client;
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _host.Services;

    public static async Task<WarpHttpTestApp> StartAsync(
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddWarpHttp();

        // Default IPublisher for tests that don't override it — needed because the test
        // assembly's source-gen registers QueueWorkHandler (which depends on IPublisher).
        builder.Services.AddSingleton<Warp.Core.IPublisher>(new Warp.Core.Testing.InMemoryPublisher());

        // Defaults for concurrency-isolation handlers. The mediator constructor eagerly
        // resolves every IRequestHandler<,> in the assembly, so these must be registered
        // even for tests that don't touch the concurrency endpoints. Tests that DO touch
        // them override these singletons in their own configureServices callback.
        builder.Services.AddSingleton<ConcurrencyIsolationTests.ScopeConstructionCounter>();
        builder.Services.AddScoped<ConcurrencyIsolationTests.ScopeProbe>();
        builder.Services.AddSingleton<ConcurrencyIsolationTests.AsyncLocalLeakRecorder>();
        builder.Services.AddSingleton<ConcurrencyIsolationTests.BehaviorInvocationSink>();

        // Register the source-gen mediator for this test assembly so IMediator and the
        // closed IRequestHandler<,> services resolve. The Warp.SourceGenerator emits
        // AddWarpMediator() into Warp.Core.Handlers.Generated of the consuming assembly.
        Warp.Core.Handlers.Generated.WarpMediatorServiceExtensions.AddWarpMediator(builder.Services);

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseRouting();
        configureApp?.Invoke(app);

        await app.StartAsync();

        var server = app.GetTestServer();
        var client = server.CreateClient();

        return new WarpHttpTestApp(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync();
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}

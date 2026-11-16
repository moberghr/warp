using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Shouldly;
using Warp.Core;
using Warp.Core.Data.Queries;
using Warp.Core.Metrics;
using Warp.Tests.TestData.Handlers;
using WarpMediator = Warp.Core.Handlers.IMediator;

namespace Warp.Tests.Core;

/// <summary>
/// R31 / SC6: a host that already runs MediatR 12.5 keeps it unchanged when it adds Warp. MediatR and
/// <c>Warp.Core.Handlers</c> share type names (<c>IRequest</c>, <c>IRequestHandler</c>, <c>IPipelineBehavior</c>,
/// <c>IMediator</c>), so this file never imports the <c>MediatR</c> namespace: every MediatR type is fully
/// qualified (CS0104 otherwise — the documented caveat).
/// </summary>
[Trait("Category", "NoDb")]
public class MediatRCoexistenceTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    [TimedFact]
    public async Task Send_MediatRRequestThroughISender_RunsTheMediatRBehaviorExactlyOnce()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var sender = scope.ServiceProvider.GetRequiredService<MediatR.ISender>();
        var result = await sender.Send(new MediatRPing("warp"), Ct);

        result.ShouldBe("pong warp");
        provider.GetRequiredService<MediatRBehaviorLog>().Invocations.ShouldBe(1);
    }

    [TimedFact]
    public async Task Send_WarpRequestThroughWarpMediator_ReturnsTheGeneratedHandlerResponse()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var mediator = scope.ServiceProvider.GetRequiredService<WarpMediator>();
        var result = await mediator.Send(new GetGreetingRequest { Name = "MediatR" }, Ct);

        result.ShouldBe("Hello, MediatR!");

        // Warp's dispatch never reached MediatR's pipeline.
        provider.GetRequiredService<MediatRBehaviorLog>().Invocations.ShouldBe(0);
    }

    [TimedFact]
    public void AddWarp_RegistersNoMediatRType_AndLeavesMediatRRegistrationsIntact()
    {
        var services = new ServiceCollection();
        RegisterMediatR(services);
        var mediatRDescriptors = services.ToList();

        RegisterWarp(services);

        // Every MediatR registration survives AddWarp, by reference: nothing was removed, replaced or decorated.
        mediatRDescriptors.ShouldAllBe(x => services.Contains(x));

        // Nothing AddWarp added is a MediatR type, closes a generic over a MediatR request, or implements a
        // MediatR contract — so Warp's generated registry neither registers nor wraps MediatR's handlers/behaviors.
        services
            .Except(mediatRDescriptors)
            .Where(x => MentionsMediatR(x.ServiceType) || MentionsMediatR(x.ImplementationType))
            .Select(x => x.ServiceType.FullName)
            .ShouldBeEmpty();

        // The Warp mediator cannot resolve the MediatR request: no Warp handler or pipeline behavior exists for it.
        services
            .Where(x => IsWarpHandlerContract(x.ServiceType))
            .Where(x => x.ServiceType.GenericTypeArguments.Any(y => typeof(MediatR.IBaseRequest).IsAssignableFrom(y)))
            .Select(x => x.ServiceType.FullName)
            .ShouldBeEmpty();
        typeof(MediatRPing).GetInterfaces().ShouldNotContain(x => x.Assembly == typeof(WarpMediator).Assembly);
    }

    [TimedFact]
    public void AddMediatR_ScanningTheTestAssembly_RegistersNoHandlerForAWarpRequest()
    {
        var services = new ServiceCollection();
        RegisterMediatR(services);
        RegisterWarp(services);

        var mediatRHandlers = services
            .Where(x => x.ServiceType.IsGenericType)
            .Where(x => x.ServiceType.GetGenericTypeDefinition() == typeof(MediatR.IRequestHandler<,>)
                || x.ServiceType.GetGenericTypeDefinition() == typeof(MediatR.IRequestHandler<>))
            .ToList();

        // Non-vacuous: the scan found the MediatR handler this file declares…
        mediatRHandlers.ShouldContain(x => x.ServiceType == typeof(MediatR.IRequestHandler<MediatRPing, string>));

        // …and nothing for any of the test assembly's Warp requests, jobs or messages.
        mediatRHandlers
            .Where(x => IsWarpRequest(x.ServiceType.GenericTypeArguments[0]))
            .Select(x => x.ServiceType.FullName)
            .ShouldBeEmpty();
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        RegisterMediatR(services);
        RegisterWarp(services);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static void RegisterMediatR(IServiceCollection services)
    {
        services.AddSingleton<MediatRBehaviorLog>();
        services.AddMediatR(x =>
        {
            x.RegisterServicesFromAssemblyContaining<MediatRPing>();
            x.AddBehavior<MediatR.IPipelineBehavior<MediatRPing, string>, MediatRCountingBehavior>();
        });
    }

    // The NoDb scaffolding DeploymentShapeTests uses: provider packages contribute these in production.
    private static void RegisterWarp(IServiceCollection services)
    {
        services.AddLogging();
        services.AddDbContext<TestContext>(x => x.UseInMemoryDatabase($"mediatr-{Guid.NewGuid():N}"));
        services.AddSingleton(Mock.Of<IWarpSqlQueries<TestContext>>());
        services.AddSingleton(Mock.Of<IWarpLockProvider>());
        services.AddWarp<TestContext>(x => x.AddMetrics(y => y.CollectMeter("MediatR.Coexistence")));
    }

    private static bool MentionsMediatR(Type? type)
    {
        if (type is null)
        {
            return false;
        }

        var mediatRAssembly = typeof(MediatR.IMediator).Assembly;

        return type.Assembly == mediatRAssembly
            || type.GetInterfaces().Any(x => x.Assembly == mediatRAssembly)
            || type.GenericTypeArguments.Any(MentionsMediatR);
    }

    private static bool IsWarpHandlerContract(Type serviceType)
        => serviceType.IsGenericType
            && serviceType.Assembly == typeof(WarpMediator).Assembly
            && string.Equals(serviceType.Namespace, typeof(WarpMediator).Namespace, StringComparison.Ordinal);

    private static bool IsWarpRequest(Type type)
        => type.GetInterfaces()
            .Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(Warp.Core.Handlers.IRequest<>));
}

public sealed record MediatRPing(string Name) : MediatR.IRequest<string>;

public sealed class MediatRPingHandler : MediatR.IRequestHandler<MediatRPing, string>
{
    public Task<string> Handle(MediatRPing request, CancellationToken cancellationToken)
        => Task.FromResult($"pong {request.Name}");
}

public sealed class MediatRCountingBehavior : MediatR.IPipelineBehavior<MediatRPing, string>
{
    private readonly MediatRBehaviorLog _log;

    public MediatRCountingBehavior(MediatRBehaviorLog log) => _log = log;

    public async Task<string> Handle(MediatRPing request, MediatR.RequestHandlerDelegate<string> next, CancellationToken cancellationToken)
    {
        _log.Increment();

        return await next();
    }
}

public sealed class MediatRBehaviorLog
{
    private int _invocations;

    public int Invocations => Volatile.Read(ref _invocations);

    public void Increment() => Interlocked.Increment(ref _invocations);
}

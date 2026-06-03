using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Warp.Core;
using Warp.Core.Handlers;
using Warp.Core.Sagas;
using Warp.Core.Services;

namespace Warp.Tests.Features.Sagas;

[Trait("Category", "NoDb")]
public class SagaServiceConfigurationTests
{
    [TimedFact]
    public void AddSagaHandler_RegistersProxyAsMessageHandler()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IWarpLockProvider, Fixtures.FakeLockProvider>();
        services.AddScoped<IJobContext, JobContext>();
        services.AddSingleton<SagaCorrelationCache>();
        services.AddScoped<ISagaStore, Fixtures.FakeSagaStore>();

        services.AddSagaHandler<OrderHandler>();

        var sp = services.BuildServiceProvider();

        var handlers = sp.GetServices<IMessageHandler<OrderStarted>>().ToList();
        handlers.Count.ShouldBe(1);
        handlers[0].ShouldBeOfType<SagaHandlerProxy<OrderSaga, OrderStarted>>();
    }

    [TimedFact]
    public void AddSagaHandler_RegistersTypedSagaHandlerInterface()
    {
        var services = new ServiceCollection();
        services.AddSagaHandler<OrderHandler>();
        var sp = services.BuildServiceProvider();

        var typed = sp.GetService<ISagaHandler<OrderSaga, OrderStarted>>();

        typed.ShouldBeOfType<OrderHandler>();
    }

    [TimedFact]
    public void AddSagaHandler_HandlerImplementsTwoInterfaces_RegistersBoth()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IWarpLockProvider, Fixtures.FakeLockProvider>();
        services.AddScoped<IJobContext, JobContext>();
        services.AddSingleton<SagaCorrelationCache>();
        services.AddScoped<ISagaStore, Fixtures.FakeSagaStore>();

        services.AddSagaHandler<OrderHandler>();

        var sp = services.BuildServiceProvider();
        sp.GetService<IMessageHandler<OrderStarted>>().ShouldBeOfType<SagaHandlerProxy<OrderSaga, OrderStarted>>();
        sp.GetService<IMessageHandler<OrderContinued>>().ShouldBeOfType<SagaHandlerProxy<OrderSaga, OrderContinued>>();
    }

    [TimedFact]
    public void AddSagaHandler_CorrelatePropertyMatchesPiiRegex_Throws()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<SagaConfigurationException>(() => services.AddSagaHandler<EmailKeyedHandler>());

        ex.Message.ShouldContain("PII-suggestive");
        ex.Message.ShouldContain("Email");
    }

    [TimedTheory]
    [InlineData("Email")]
    [InlineData("EmailAddress")]
    [InlineData("Phone")]
    [InlineData("PhoneNumber")]
    [InlineData("Ssn")]
    [InlineData("SocialSecurityNumber")]
    [InlineData("TaxId")]
    [InlineData("CreditCard")]
    [InlineData("CardNumber")]
    [InlineData("Password")]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("FullName")]
    [InlineData("DateOfBirth")]
    [InlineData("Dob")]
    public void PiiNameRegex_MatchesAllBlockedPatterns(string propertyName)
    {
        SagaPiiCheck.IsPiiName(propertyName).ShouldBeTrue($"'{propertyName}' should be flagged as PII");
    }

    [TimedTheory]
    [InlineData("OrderId")]
    [InlineData("AccountId")]
    [InlineData("CustomerNumber")]
    [InlineData("UserName")] // not in the regex on purpose; doc trade-off
    [InlineData("TenantId")]
    public void PiiNameRegex_DoesNotMatchOpaqueIdentifiers(string propertyName)
    {
        SagaPiiCheck.IsPiiName(propertyName).ShouldBeFalse($"'{propertyName}' should pass the PII guard");
    }

    [TimedFact]
    public void AddSagaHandler_PiiPropertyMarkedAnonymized_Succeeds()
    {
        var services = new ServiceCollection();

        services.AddSagaHandler<AnonymizedEmailKeyedHandler>();

        // No throw — proxy is registered.
        services.Any(d => d.ServiceType == typeof(IMessageHandler<AnonymizedEmailKeyedMessage>)).ShouldBeTrue();
    }

    [TimedFact]
    public void AddSagaHandler_TypeWithoutSagaHandlerInterface_Throws()
    {
        var services = new ServiceCollection();

        var ex = Should.Throw<InvalidOperationException>(() => services.AddSagaHandler<NotASagaHandler>());

        ex.Message.ShouldContain("does not implement ISagaHandler");
    }

    [TimedFact]
    public void AddSagas_RegistersStoreAsScoped()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWarpLockProvider, Fixtures.FakeLockProvider>();
        var builder = new WarpBuilder<TestContext>(services);

        builder.AddSagas();

        services.Any(d => d.ServiceType == typeof(ISagaStore) && d.Lifetime == ServiceLifetime.Scoped).ShouldBeTrue();
        services.Any(d => d.ServiceType == typeof(SagaCorrelationCache) && d.Lifetime == ServiceLifetime.Singleton).ShouldBeTrue();
    }

    [TimedFact]
    public void AddSagas_CalledTwice_DoesNotDoubleRegisterServices()
    {
        // TryAdd contract: a second AddSagas() call is a no-op. Pin this so a future
        // regression that swaps TryAddScoped/TryAddSingleton back to plain Add/AddSingleton
        // (which would double-register, with SagaCorrelationCache as the worst case since
        // it's a singleton and two instances diverge) doesn't pass silently.
        var services = new ServiceCollection();
        services.AddSingleton<IWarpLockProvider, Fixtures.FakeLockProvider>();
        var builder = new WarpBuilder<TestContext>(services);

        builder.AddSagas();
        builder.AddSagas();
        builder.AddSagas();

        services.Count(d => d.ServiceType == typeof(ISagaStore)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(SagaCorrelationCache)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(ISagaQueryService)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(ISagaCommandService)).ShouldBe(1);
    }

    [TimedFact]
    public void AddSagas_WithoutLockProvider_Throws()
    {
        var services = new ServiceCollection();
        var builder = new WarpBuilder<TestContext>(services);

        var ex = Should.Throw<InvalidOperationException>(() => builder.AddSagas());

        ex.Message.ShouldContain("IWarpLockProvider");
        ex.Message.ShouldContain("UsePostgreSql");
        ex.Message.ShouldContain("UseSqlServer");
    }

    public sealed class OrderSaga : Saga
    {
        public bool Started { get; set; }
    }

    [StartsSaga]
    public sealed class OrderStarted : IMessage
    {
        [Correlate]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class OrderContinued : IMessage
    {
        [Correlate]
        public string OrderId { get; set; } = string.Empty;
    }

    public sealed class OrderHandler :
        ISagaHandler<OrderSaga, OrderStarted>,
        ISagaHandler<OrderSaga, OrderContinued>
    {
        public Task HandleAsync(OrderSaga saga, OrderStarted message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task HandleAsync(OrderSaga saga, OrderContinued message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class NotASagaHandler;

    public sealed class EmailKeyedMessage : IMessage
    {
        [Correlate]
        public string Email { get; set; } = string.Empty;
    }

    public sealed class AnonymizedEmailKeyedMessage : IMessage
    {
        [Correlate(IsAnonymized = true)]
        public string Email { get; set; } = string.Empty;
    }

    public sealed class EmailKeyedHandler : ISagaHandler<OrderSaga, EmailKeyedMessage>
    {
        public Task HandleAsync(OrderSaga saga, EmailKeyedMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class AnonymizedEmailKeyedHandler : ISagaHandler<OrderSaga, AnonymizedEmailKeyedMessage>
    {
        public Task HandleAsync(OrderSaga saga, AnonymizedEmailKeyedMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

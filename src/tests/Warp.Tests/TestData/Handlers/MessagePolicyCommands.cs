using Warp.Core.Concurrency;
using Warp.Core.Enums;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

// Addon policy axis fixtures for MESSAGES: contract-declared policy is copied to every handler's
// child job (shared key); handler-declared policy applies to that handler's children only.

// SC11 — contract axis: [Mutex] on the MESSAGE; both handlers' children carry the key and contend.
[Mutex("msg-contract-mutex")]
public class ContractMutexMessage : IMessage;

public class ContractMutexHandlerA : IMessageHandler<ContractMutexMessage>
{
    private readonly BarrierSignal _signal;

    public ContractMutexHandlerA(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(ContractMutexMessage message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

public class ContractMutexHandlerB : IMessageHandler<ContractMutexMessage>
{
    private readonly BarrierSignal _signal;

    public ContractMutexHandlerB(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(ContractMutexMessage message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

// SC12 — handler axis: the MESSAGE carries nothing; only HandlerMutexMessageHandlerA is serialized,
// the plain handler's children run unconstrained.
public class HandlerMutexMessage : IMessage;

[Mutex("msg-handler-mutex")]
public class HandlerMutexMessageHandlerA : IMessageHandler<HandlerMutexMessage>
{
    private readonly BarrierSignal _signal;

    public HandlerMutexMessageHandlerA(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(HandlerMutexMessage message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

public class HandlerMutexMessagePlainHandler : IMessageHandler<HandlerMutexMessage>
{
    public Task HandleAsync(HandlerMutexMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

// SC13 — Wait-mode handler mutex on a routed message: the bounced child must keep HandlerType on
// requeue (§8.14) and complete once the slot frees; a cleared HandlerType would fail re-dispatch
// with "No handler registered" (messages are discovered via IMessageHandler, not IJobHandler).
public class WaitMutexMessage : IMessage;

[Mutex("msg-wait-mutex", Mode = ConcurrencyMode.Wait)]
public class WaitMutexMessageHandler : IMessageHandler<WaitMutexMessage>
{
    private readonly BarrierSignal _signal;

    public WaitMutexMessageHandler(BarrierSignal signal)
    {
        _signal = signal;
    }

    public async Task HandleAsync(WaitMutexMessage message, CancellationToken cancellationToken)
    {
        _signal.Running.Release();
        await _signal.CanFinish.WaitAsync(cancellationToken);
    }
}

// SC14 — handler-declared [Retry] on a message handler; the sibling plain handler is unaffected.
public class RetryPolicyMessage : IMessage;

[Retry(2, Delays = [1])]
public class RetryPolicyMessageFailingHandler : IMessageHandler<RetryPolicyMessage>
{
    public Task HandleAsync(RetryPolicyMessage message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Always fails");
}

public class RetryPolicyMessagePlainHandler : IMessageHandler<RetryPolicyMessage>
{
    public Task HandleAsync(RetryPolicyMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

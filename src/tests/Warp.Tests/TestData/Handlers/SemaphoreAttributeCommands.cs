using Warp.Core.Concurrency;
using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

public class SemaphoreAttributeCommand : IJobHandler<SemaphoreAttributeRequest>
{
    public Task HandleAsync(SemaphoreAttributeRequest message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

[Semaphore("static-semaphore-key", 5)]
public class SemaphoreAttributeRequest : IJob;

public class SemaphoreSkipAttributeCommand : IJobHandler<SemaphoreSkipAttributeRequest>
{
    public Task HandleAsync(SemaphoreSkipAttributeRequest message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

[Semaphore("static-semaphore-skip-key", 5, Mode = ConcurrencyMode.Skip)]
public class SemaphoreSkipAttributeRequest : IJob;

public class MutexAndSemaphoreCommand : IJobHandler<MutexAndSemaphoreRequest>
{
    public Task HandleAsync(MutexAndSemaphoreRequest message, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

// When both attributes are present, [Mutex] wins (family order in PolicyResolver.ConcurrencyFamily).
[Mutex("dual-attribute-key")]
[Semaphore("dual-attribute-key", 5)]
public class MutexAndSemaphoreRequest : IJob;

public class ThrowingConcurrencyCommand : IJobHandler<ThrowingConcurrencyRequest>
{
    public Task HandleAsync(ThrowingConcurrencyRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails — characterization test for slot release");
    }
}

public class ThrowingConcurrencyRequest : IJob;

using Warp.Core.Handlers;

namespace Warp.Tests.TestData.Handlers;

// Handler with [Retry] attribute
[Retry(5)]
public class RetryAttributeHandlerCommand : IJobHandler<RetryAttributeHandlerRequest>
{
    public Task HandleAsync(RetryAttributeHandlerRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails");
    }
}

public class RetryAttributeHandlerRequest : IJob;

// Job class with [Retry] attribute, handler without attribute
public class RetryAttributeJobCommand : IJobHandler<RetryAttributeJobRequest>
{
    public Task HandleAsync(RetryAttributeJobRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails");
    }
}

[Retry(4)]
public class RetryAttributeJobRequest : IJob;

// Both handler and job have [Retry] — handler should win
[Retry(7)]
public class RetryAttributeBothCommand : IJobHandler<RetryAttributeBothRequest>
{
    public Task HandleAsync(RetryAttributeBothRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails");
    }
}

[Retry(2)]
public class RetryAttributeBothRequest : IJob;

// Handler with [Retry] that includes custom delays
[Retry(3, Delays = [100, 200, 300])]
public class RetryAttributeWithDelaysCommand : IJobHandler<RetryAttributeWithDelaysRequest>
{
    public Task HandleAsync(RetryAttributeWithDelaysRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails");
    }
}

public class RetryAttributeWithDelaysRequest : IJob;

// #236 regression: job class carrying [Retry(3)], published through the REAL publisher (no explicit
// metadata). Global default is 1 retry; the attribute must win (3 retries). Kept at 3 so the retry
// chain matches the timing of the proven GivenFailingJobWithThreeRetries integration test.
public class RetryAttributeThreeJobCommand : IJobHandler<RetryAttributeThreeJobRequest>
{
    public Task HandleAsync(RetryAttributeThreeJobRequest message, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Always fails");
    }
}

[Retry(3)]
public class RetryAttributeThreeJobRequest : IJob;

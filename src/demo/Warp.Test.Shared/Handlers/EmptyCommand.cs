using Warp.Core.Handlers;

namespace Warp.Core.Handlers;

public class EmptyCommand : IJobHandler<EmptyRequest>
{
    public Task HandleAsync(EmptyRequest message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class EmptyRequest : IJob;

public class EmptyMessage : IMessage;

public class EmptyMessageHandler1 : IMessageHandler<EmptyMessage>
{
    public Task HandleAsync(EmptyMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class EmptyMessageHandler2 : IMessageHandler<EmptyMessage>
{
    public Task HandleAsync(EmptyMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class EmptyMessageHandler3 : IMessageHandler<EmptyMessage>
{
    public Task HandleAsync(EmptyMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A no-op job carrying a sized payload, so benchmarks can measure how job width affects the
/// worker's write path. An empty-payload benchmark cannot see row-rewrite cost at all.
/// </summary>
public class PayloadRequest : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand : IJobHandler<PayloadRequest>
{
    public Task HandleAsync(PayloadRequest message, CancellationToken cancellationToken) => Task.CompletedTask;
}

// Distinct job types for benchmarking counter-key cardinality. Every finalizing job emits ~20
// counter keys derived from its type and handler, so a single-type workload collapses to ~20 keys
// however many jobs run — which flatters in-memory pre-aggregation. These exist so the collapse can
// be measured against a realistic spread of types instead.
public class PayloadRequest1 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand1 : IJobHandler<PayloadRequest1>
{
    public Task HandleAsync(PayloadRequest1 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest2 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand2 : IJobHandler<PayloadRequest2>
{
    public Task HandleAsync(PayloadRequest2 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest3 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand3 : IJobHandler<PayloadRequest3>
{
    public Task HandleAsync(PayloadRequest3 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest4 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand4 : IJobHandler<PayloadRequest4>
{
    public Task HandleAsync(PayloadRequest4 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest5 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand5 : IJobHandler<PayloadRequest5>
{
    public Task HandleAsync(PayloadRequest5 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest6 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand6 : IJobHandler<PayloadRequest6>
{
    public Task HandleAsync(PayloadRequest6 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest7 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand7 : IJobHandler<PayloadRequest7>
{
    public Task HandleAsync(PayloadRequest7 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

public class PayloadRequest8 : IJob
{
    public string Data { get; set; } = string.Empty;
}

public class PayloadCommand8 : IJobHandler<PayloadRequest8>
{
    public Task HandleAsync(PayloadRequest8 message, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A job that spends a configurable time doing nothing, to model handler work the benchmark can hold
/// constant. <c>EmptyRequest</c> measures Warp's overhead in isolation; this measures what that
/// overhead is worth once a job also does something.
/// </summary>
public class DelayRequest : IJob
{
    public int DelayMs { get; set; }
}

public class DelayCommand : IJobHandler<DelayRequest>
{
    public async Task HandleAsync(DelayRequest message, CancellationToken cancellationToken)
    {
        if (message.DelayMs > 0)
        {
            await Task.Delay(message.DelayMs, cancellationToken);
        }
    }
}

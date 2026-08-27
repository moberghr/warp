namespace Warp.Core.Handlers;

/// <summary>
/// Marker for handlers that manage their own execution policy and must not be wrapped by the policy
/// pipeline behaviours (Concurrency, RateLimit, Timeout, Retry, CircuitBreaker) — declared attributes,
/// stamped metadata and global defaults are all skipped for executions bound to an implementing handler.
/// <see cref="Warp.Core.Sagas.SagaHandlerProxy{TSaga, TMessage}"/> is the canonical case: it serializes on
/// its own per-correlation mutex, reschedules its own busy/version conflicts, and commits saga state inside
/// the handler scope — an outer timeout or retry would race that machinery.
/// </summary>
public interface IPolicyExemptHandler;

using System.Reflection;
using Warp.Core.Handlers;

namespace Warp.Core.Retry;

/// <summary>
/// Freezes the job-type <see cref="RetryAttribute"/> into metadata at publish (mirroring the concurrency
/// and rate-limit publish behaviors). It must <b>not</b> stamp the global <see cref="RetryOptions"/>
/// default: doing so populated <c>meta.MaxRetries</c> for every job and shadowed a handler-declared
/// <c>[Retry]</c> at execution — <c>metadata ?? attribute ?? options</c> never reached the attribute
/// (issue #236). The global default is applied at execution via <c>IOptions</c>, and a handler-level
/// attribute is resolved there by <see cref="RetryPipelineBehavior{TRequest,TResponse}"/>.
/// </summary>
public class RetryPublishBehavior<T> : IPublishPipelineBehavior<T>
{
    private static readonly RetryAttribute? Attribute = typeof(T).GetCustomAttribute<RetryAttribute>();

    public Task PublishAsync(PublishContext<T> context, PublishDelegate next, CancellationToken ct)
    {
        if (Attribute is not null)
        {
            var meta = context.GetMetadata<IRetryMetadata>();

            meta.MaxRetries ??= Attribute.MaxRetries;

            if (Attribute.Delays is { Length: > 0 } && meta.RetryDelays is null)
            {
                meta.RetryDelays = Attribute.Delays;
            }
        }

        return next();
    }
}

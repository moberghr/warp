using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

/// <summary>
/// Creates an <see cref="IPublisher"/> or <see cref="IBatchPublisher"/> bound to a
/// <typeparamref name="TContext"/> instance the caller supplies, instead of the scope's
/// <typeparamref name="TContext"/>. Use it when the business write happens on a context that is not
/// the scoped one, typically one from <c>IDbContextFactory&lt;TContext&gt;</c> (Blazor Server, where
/// the scoped context lives as long as the circuit). The returned publisher stages jobs on
/// <c>context</c>, so one <c>SaveChangesAsync</c> commits the business state and the jobs together.
/// It runs the same <c>IPublishPipelineBehavior&lt;T&gt;</c> chain as the scoped publisher, and its
/// <c>SaveChangesAsync</c> dispatches the same enqueue notifications.
/// Registered as a scoped service by <c>AddWarp</c>; it does not need <typeparamref name="TContext"/>
/// to be registered as a scoped service.
/// </summary>
/// <typeparam name="TContext">The DbContext type Warp was configured with.</typeparam>
public interface IPublisherFactory<in TContext>
    where TContext : DbContext
{
    /// <summary>Returns a publisher that stages onto <paramref name="context"/>. The caller owns
    /// the context; the publisher does not dispose it.</summary>
    IPublisher Create(TContext context);

    /// <summary>Returns a batch publisher that stages onto <paramref name="context"/>. The caller
    /// owns the context; the publisher does not dispose it.</summary>
    IBatchPublisher CreateBatch(TContext context);
}

internal sealed class PublisherFactory<TContext> : IPublisherFactory<TContext>
    where TContext : DbContext
{
    private readonly Func<TContext, IPublisher> _createPublisher;
    private readonly Func<TContext, IBatchPublisher> _createBatchPublisher;

    public PublisherFactory(Func<TContext, IPublisher> createPublisher, Func<TContext, IBatchPublisher> createBatchPublisher)
    {
        _createPublisher = createPublisher;
        _createBatchPublisher = createBatchPublisher;
    }

    public IPublisher Create(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _createPublisher(context);
    }

    public IBatchPublisher CreateBatch(TContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return _createBatchPublisher(context);
    }
}

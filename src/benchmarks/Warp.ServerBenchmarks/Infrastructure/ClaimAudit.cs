using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Warp.Core.Data.Queries;
using Warp.Core.Entities;

namespace Warp.ServerBenchmarks.Infrastructure;

/// <summary>
/// Wraps the provider's <see cref="IWarpSqlQueries{TContext}"/> and reports any claim that returns more
/// rows than it asked for. The worker asks for one and runs only <c>claimed[0]</c>, so a surplus row is
/// left Processing with no log and no worker — which is what an intermittent drain timeout on the
/// concurrency arm left behind, several rows at a time, all stamped with one worker and one claim time.
/// Harness-only: Warp itself is untouched.
/// </summary>
internal class ClaimAudit<TContext> : DispatchProxy
    where TContext : Microsoft.EntityFrameworkCore.DbContext
{
    private IWarpSqlQueries<TContext> _inner = null!;

    public static void Install(IServiceCollection services)
    {
        var descriptor = services.Last(x => x.ServiceType == typeof(IWarpSqlQueries<TContext>));
        services.Remove(descriptor);
        services.AddSingleton(sp =>
        {
            var inner = (IWarpSqlQueries<TContext>)(descriptor.ImplementationInstance
                ?? descriptor.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!));
            var proxy = Create<IWarpSqlQueries<TContext>, ClaimAudit<TContext>>();
            ((ClaimAudit<TContext>)(object)proxy)._inner = inner;

            return proxy;
        });
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = targetMethod!.Invoke(_inner, args);

        if (!string.Equals(targetMethod.Name, nameof(IWarpSqlQueries<TContext>.ClaimEnqueuedJobsAsync), StringComparison.Ordinal)
            || result is not Task<List<Job>> task)
        {
            return result;
        }

        var limit = (int)args![4]!;

        return task.ContinueWith(
            x =>
            {
                var claimed = x.GetAwaiter().GetResult();
                if (claimed.Count > limit)
                {
                    Console.WriteLine(
                        $"// OVER-CLAIM asked={limit} got={claimed.Count} worker={args[2]} now={(DateTime)args[3]!:O} "
                        + $"ids={string.Join(",", claimed.Select(y => y.Id))}");
                }

                return claimed;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

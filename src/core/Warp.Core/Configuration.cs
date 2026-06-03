using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Warp.Core;

public class WarpConfiguration
{
    public string DefaultQueue { get; set; } = "default";

    /// <summary>
    /// Assemblies whose IRequestHandler / IJobHandler / IMessageHandler /
    /// IStreamRequestHandler registrations should be removed after the source generator
    /// applies them. The generator unconditionally registers every handler it discovers
    /// across the current project and its references; this list lets the host opt
    /// specific assemblies out — typical use is a multi-host solution where one host
    /// references a sibling host's handlers transitively and doesn't want them in DI.
    /// Populated via <c>opt.ExcludeHandlersFromAssembly(...)</c>.
    /// </summary>
    internal HashSet<Assembly> ExcludedHandlerAssemblies { get; } = [];

    public string? Schema { get; set; } = "warp";

    /// <summary>
    /// How long completed and deleted jobs are retained before cleanup.
    /// Failed jobs are never auto-expired.
    /// </summary>
    public TimeSpan JobExpirationTimeout { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Extension point for external/third-party addons (e.g. provider packages) to
    /// contribute entities to the Warp DbContext model. Invoked by WarpModelCustomizer
    /// after the core and addon entities are registered.
    /// <para>
    /// In-tree addons (CircuitBreaker, Concurrency, RateLimit, Sagas) do NOT use this
    /// list — their entities are registered unconditionally by WarpModelCustomizer so a
    /// single migration covers every deployment shape regardless of which hosts opt in
    /// to the runtime behavior.
    /// </para>
    /// </summary>
    public List<Action<ModelBuilder, string?>> EntityConfigurators { get; } = [];

    /// <summary>
    /// How long the host waits for each <c>WarpBackgroundService.ExecuteAsync</c> to return
    /// after the cancellation token is signalled during graceful shutdown. Services that do not
    /// observe cancellation are abandoned at process exit — same semantics as plain
    /// <c>BackgroundService.StopAsync</c> with a timeout.
    /// </summary>
    public TimeSpan BackgroundServiceShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Global default for the maximum number of captured log rows retained per
    /// <c>WarpBackgroundService</c> instance. Oldest rows are deleted by
    /// <c>ExpirationCleanup</c> when the count exceeds this value. Per-service overrides
    /// via <c>WarpBackgroundService.LogRetentionCountOverride</c> take precedence.
    /// </summary>
    public int BackgroundServiceLogRetentionCount { get; set; } = 1000;

    /// <summary>
    /// Global default for the maximum age of captured log rows. Rows older than this value
    /// are deleted by <c>ExpirationCleanup</c>. Per-service overrides via
    /// <c>WarpBackgroundService.LogRetentionAgeOverride</c> take precedence.
    /// </summary>
    public TimeSpan BackgroundServiceLogRetentionAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Grace window before an orphaned <c>BackgroundServiceDefinition</c> row is deleted by
    /// <c>ExpirationCleanup</c>. A Definition is considered orphaned when no live
    /// <c>BackgroundServiceInstance</c> references its name AND its <c>LastSeenAt</c> is
    /// older than this value. The grace exists solely to absorb the rolling-deploy gap
    /// between server A's exit (its Instance is cleaned) and server B's startup
    /// registration — without it the Definition would be deleted and immediately recreated,
    /// losing <c>FirstSeenAt</c> history. Increase for environments with longer deploys.
    /// </summary>
    public TimeSpan BackgroundServiceDefinitionOrphanGrace { get; set; } = TimeSpan.FromMinutes(2);
}

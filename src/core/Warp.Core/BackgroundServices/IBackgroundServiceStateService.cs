namespace Warp.Core.BackgroundServices;

/// <summary>
/// Outcome of a <see cref="IBackgroundServiceStateService.RegisterAsync"/> call.
/// </summary>
public enum RegistrationOutcome
{
    /// <summary>
    /// The definition was inserted or updated and the instance row was inserted for this server.
    /// The supervisor may proceed to execute the service.
    /// </summary>
    Registered = 1,

    /// <summary>
    /// The stored <c>BackgroundServiceDefinition.DeclaredScope</c> does not match the scope the
    /// registering host declared. The instance row has been inserted with
    /// <c>Status = ConfigurationMismatch</c> — the supervisor must not start the service.
    /// </summary>
    ConfigurationMismatch = 2,
}

/// <summary>
/// Manages <c>BackgroundServiceInstance</c> and <c>BackgroundServiceDefinition</c> rows for
/// services running on this server. All methods are scoped to the calling server's
/// <c>ServerId</c>, resolved via <c>WarpServerConfiguration</c>.
/// </summary>
public interface IBackgroundServiceStateService
{
    /// <summary>
    /// Upserts the <c>BackgroundServiceDefinition</c> by <paramref name="serviceName"/>
    /// (insert-if-missing, always update <c>LastSeenAt</c>), then inserts or updates the
    /// <c>BackgroundServiceInstance</c> row for <c>(@me, serviceName)</c> with the declared
    /// scope and a status determined by the scope comparison:
    /// <list type="bullet">
    /// <item>New definition or matching scope → <c>Waiting</c> (Singleton) or <c>Running</c>
    /// (PerServer); returns <see cref="RegistrationOutcome.Registered"/>.</item>
    /// <item>Mismatched scope → <c>ConfigurationMismatch</c>; returns
    /// <see cref="RegistrationOutcome.ConfigurationMismatch"/>.</item>
    /// </list>
    /// </summary>
    Task<RegistrationOutcome> RegisterAsync(string serviceName, ServiceScope declaredScope, CancellationToken ct);

    /// <summary>
    /// Updates <c>Status</c> on the instance row for <c>(@me, serviceName)</c>.
    /// </summary>
    Task SetStatusAsync(string serviceName, BackgroundServiceStatus status, CancellationToken ct);

    /// <summary>
    /// Transitions the instance to <c>Faulted</c>, persists <c>LastError</c> (type + message,
    /// capped at 4 KB), stamps <c>LastErrorAt</c>, and increments <c>RestartCount</c>.
    /// </summary>
    Task RecordFaultAsync(string serviceName, Exception ex, CancellationToken ct);

    /// <summary>
    /// Resets <c>RestartCount</c> to zero on the instance row for <c>(@me, serviceName)</c>.
    /// Used by the supervisor's healthy-reset logic when a fault follows a sustained healthy run.
    /// </summary>
    Task ResetRestartCountAsync(string serviceName, CancellationToken ct);

    /// <summary>
    /// Removes the <c>BackgroundServiceInstance</c> row for <c>(@me, serviceName)</c>. Called on
    /// graceful shutdown; ungraceful cleanup is handled by <c>ServerCleanup</c>.
    /// </summary>
    Task DeleteAsync(string serviceName, CancellationToken ct);

    /// <summary>
    /// Returns the <c>DeclaredScope</c> stored in the <c>BackgroundServiceDefinition</c> row for
    /// <paramref name="serviceName"/>, or <see langword="null"/> if no definition row exists yet.
    /// Used by the supervisor on the <see cref="RegistrationOutcome.ConfigurationMismatch"/> path
    /// to log which scope the definition row holds vs. what this host declared.
    /// </summary>
    Task<ServiceScope?> GetDefinedScopeAsync(string serviceName, CancellationToken ct);
}

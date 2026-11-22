namespace Warp.Core.Handlers;

/// <summary>
/// Carries scope-bound state into the scope the mediator creates for one send when
/// <see cref="WarpMediatorOptions.ScopePerSend"/> is on. Register one per piece of state, next to the
/// state it copies; every registered initializer runs, in registration order, before the handler is
/// resolved.
/// <code>
/// public sealed class TenantScopeInitializer : ISendScopeInitializer
/// {
///     public void Initialize(IServiceProvider parent, IServiceProvider child)
///     {
///         var tenant = parent.GetRequiredService&lt;TenantScope&gt;();
///         if (tenant.IsSet)
///         {
///             child.GetRequiredService&lt;TenantScope&gt;().Set(tenant.Id);
///         }
///     }
/// }
/// </code>
/// </summary>
public interface ISendScopeInitializer
{
    /// <summary>Copies what the handler needs from <paramref name="parent"/> into <paramref name="child"/>.</summary>
    /// <param name="parent">The scope the mediator was resolved from.</param>
    /// <param name="child">The new scope the handler will run in.</param>
    void Initialize(IServiceProvider parent, IServiceProvider child);
}

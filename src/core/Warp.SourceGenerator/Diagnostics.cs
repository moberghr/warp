using Microsoft.CodeAnalysis;

namespace Warp.SourceGenerator;

/// <summary>
/// Build-time diagnostics for the addon policy axis. The runtime half of this validation lives in
/// <c>ServiceConfiguration.ValidateAddonAttributesOnHandlers</c> and deliberately stays: it is the
/// backstop for handlers this generator cannot see, and it owns the one rule that is not a type-level
/// fact. See <see cref="PolicyAxisValidator"/> for the split.
/// </summary>
internal static class Diagnostics
{
    private const string Category = "Warp";

    public static readonly DiagnosticDescriptor PolicyOnBothAxes = new(
        id: "WARP001",
        title: "Addon policy declared on both the contract and its handler",
        messageFormat: "{0} policy is declared on BOTH '{1}' (the contract) and '{2}' (its handler). The contract declaration is stamped into metadata at publish and would silently shadow the handler one — pick one axis and delete the other declaration.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PolicyOnUnsupportedHandler = new(
        id: "WARP002",
        title: "Addon policy attribute on a handler shape that cannot honour it",
        messageFormat: "[{0}] is declared on handler '{1}', where it is silently ignored: '{2}' is not an IJob or IMessage, so no execution path can honour a handler-declared policy there. Declare the policy on a job/message handler, or on the request/job type. See issue #242.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TotalTimeoutOnHandler = new(
        id: "WARP003",
        title: "Total-scoped [Timeout] declared on a handler",
        messageFormat: "[Timeout(Scope = Total)] is declared on handler '{0}', but a Total-scoped timeout is a wall-clock budget measured from enqueue — its deadline must be stamped at publish, before any handler is known. Declare Total-scoped timeouts on the request/job type; PerAttempt timeouts may stay on the handler.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

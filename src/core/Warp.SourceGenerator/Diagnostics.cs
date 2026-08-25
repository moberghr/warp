using Microsoft.CodeAnalysis;

namespace Warp.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "Warp";

    public static readonly DiagnosticDescriptor PolicyOnUnsupportedHandler = new(
        id: "WARP001",
        title: "Addon policy attribute on a handler shape that cannot honour it",
        messageFormat: "[{0}] is declared on handler '{1}', where it is silently ignored: '{2}' is not an IJob or IMessage, so no execution path can honour a handler-declared policy there. Declare the policy on a job/message handler, or on the request/job type.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor TotalTimeoutOnHandler = new(
        id: "WARP002",
        title: "Total-scoped [Timeout] declared on a handler",
        messageFormat: "[Timeout(Scope = Total)] is declared on handler '{0}', but a Total-scoped timeout is a wall-clock budget measured from enqueue — its deadline must be stamped at publish, before any handler is known. Declare Total-scoped timeouts on the request/job type; PerAttempt timeouts may stay on the handler.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

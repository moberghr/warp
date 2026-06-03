using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Warp.Http.SourceGenerator.Emitters;

/// <summary>
/// Emits a per-endpoint static class with a <c>Handler</c> delegate that ASP.NET
/// Minimal API binds. Three shapes:
/// <list type="bullet">
/// <item><b>WholeBody</b> — <c>(HttpContext, TRequest)</c>; ASP.NET binds the body
/// directly. The natural shape for plain POST DTOs with no per-property attributes.</item>
/// <item><b>AsParameters</b> — <c>(HttpContext, [AsParameters] TRequest)</c>; ASP.NET
/// decomposes per property. Used when there is no body part.</item>
/// <item><b>Mixed</b> — explicit lambda parameters per binding source plus a
/// <c>[FromBody]</c> body parameter. The generator constructs <c>TRequest</c> in
/// the lambda. Required because <c>[AsParameters]</c> does not support
/// <c>[FromBody]</c> properties.</item>
/// </list>
/// </summary>
internal static class DelegateEmitter
{
    public static void Emit(StringBuilder sb, HttpEndpointModel ep, BindingPlan plan, int index)
    {
        var requestFqn = ep.RequestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var responseFqn = ep.ResponseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var invocation = ep.Kind == HttpHandlerKind.Stream ? "InvokeStream" : "InvokeRequest";

        sb.Append("    internal static class WarpHttpDelegate_").Append(index).AppendLine();
        sb.AppendLine("    {");
        sb.AppendLine("        public static global::System.Delegate Handler { get; } = ");

        switch (plan.Shape)
        {
            case BindingShape.WholeBody:
                EmitWholeBodyHandler(sb, requestFqn, responseFqn, invocation);
                break;

            case BindingShape.AsParameters:
                EmitAsParametersHandler(sb, requestFqn, responseFqn, invocation);
                break;

            case BindingShape.Mixed:
                EmitMixedHandler(sb, plan, requestFqn, responseFqn, invocation);
                break;
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void EmitWholeBodyHandler(StringBuilder sb, string requestFqn, string responseFqn, string invocation)
    {
        sb.Append("            static (global::Microsoft.AspNetCore.Http.HttpContext ctx, ")
            .Append(requestFqn).AppendLine(" request) =>");
        sb.Append("                global::Warp.Http.Dispatch.WarpHttpInvocation.").Append(invocation).Append('<')
            .Append(requestFqn).Append(", ").Append(responseFqn).AppendLine(">(ctx, request);");
    }

    private static void EmitAsParametersHandler(StringBuilder sb, string requestFqn, string responseFqn, string invocation)
    {
        sb.Append("            static (global::Microsoft.AspNetCore.Http.HttpContext ctx, [global::Microsoft.AspNetCore.Http.AsParameters] ")
            .Append(requestFqn).AppendLine(" request) =>");
        sb.Append("                global::Warp.Http.Dispatch.WarpHttpInvocation.").Append(invocation).Append('<')
            .Append(requestFqn).Append(", ").Append(responseFqn).AppendLine(">(ctx, request);");
    }

    private static void EmitMixedHandler(StringBuilder sb, BindingPlan plan, string requestFqn, string responseFqn, string invocation)
    {
        // Each binding target maps to either a lambda parameter (with the right [FromX] attribute)
        // or a [FromBody] parameter. We keep the lambda parameter list ordered:
        //   1. HttpContext
        //   2. Each non-body target in declaration order, [FromX]-attributed
        //   3. The single body target (if any), [FromBody]-attributed, with TRequest body shape
        //
        // Inside the lambda, we construct TRequest from the bound parts and dispatch.
        var bodyTargetCount = plan.Targets.Count(t => t.Source == BindingSource.Body);
        if (bodyTargetCount > 1)
        {
            // Minimal API only accepts one body parameter, and the param-name dictionary below
            // only captures the first body target — so emission would fail with a cryptic
            // KeyNotFoundException on construction. This case must be diagnosed earlier as
            // WHTTP004 in WarpHttpGenerator.ProcessCandidate. Throwing here is defense-in-depth:
            // if the gate is ever bypassed, the outer try/catch surfaces a clear WHTTP999.
            throw new InvalidOperationException(
                "EmitMixedHandler invariant violated: BindingPlan has "
                + bodyTargetCount
                + " body-bound targets but at most one is supported. This should have been diagnosed as WHTTP004 before reaching emission.");
        }

        sb.Append("            static async global::System.Threading.Tasks.Task (global::Microsoft.AspNetCore.Http.HttpContext ctx");

        var bodyTarget = plan.Targets.FirstOrDefault(t => t.Source == BindingSource.Body);
        var nonBodyTargets = plan.Targets.Where(t => t.Source != BindingSource.Body).ToArray();
        var paramNames = new Dictionary<BindingTarget, string>();

        for (var i = 0; i < nonBodyTargets.Length; i++)
        {
            var target = nonBodyTargets[i];
            var attribute = target.Source switch
            {
                BindingSource.Route => $"[global::Microsoft.AspNetCore.Mvc.FromRoute(Name = \"{Escape(target.SourceKey)}\")]",
                BindingSource.Query => $"[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"{Escape(target.SourceKey)}\")]",
                BindingSource.Header => $"[global::Microsoft.AspNetCore.Mvc.FromHeader(Name = \"{Escape(target.SourceKey)}\")]",
                _ => string.Empty,
            };

            var paramName = "p" + i;
            paramNames[target] = paramName;
            var typeFqn = target.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.Append(", ").Append(attribute).Append(' ').Append(typeFqn).Append(' ').Append(paramName);
        }

        if (bodyTarget is not null)
        {
            var bodyTypeFqn = bodyTarget.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            sb.Append(", [global::Microsoft.AspNetCore.Mvc.FromBody] ").Append(bodyTypeFqn).Append(" body");
            paramNames[bodyTarget] = "body";
        }

        sb.AppendLine(") =>");
        sb.AppendLine("            {");

        // Construction expression — order targets by ctor parameter index (records) or just emit
        // a property initializer (parameterless ctor + setters).
        sb.Append("                var request = ");
        EmitConstruction(sb, plan, requestFqn, paramNames);
        sb.AppendLine(";");

        sb.Append("                await global::Warp.Http.Dispatch.WarpHttpInvocation.").Append(invocation)
            .Append('<').Append(requestFqn).Append(", ").Append(responseFqn).AppendLine(">(ctx, request).ConfigureAwait(false);");
        sb.AppendLine("            };");
    }

    private static void EmitConstruction(StringBuilder sb, BindingPlan plan, string requestFqn, Dictionary<BindingTarget, string> paramNames)
    {
        if (plan.UsesPrimaryCtor)
        {
            sb.Append("new ").Append(requestFqn).Append('(');
            var ordered = plan.Targets.OrderBy(t => t.CtorParameterIndex ?? 0).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append(paramNames[ordered[i]]);
            }

            sb.Append(')');
            return;
        }

        sb.Append("new ").Append(requestFqn).Append("()");
        if (plan.Targets.Count == 0)
        {
            return;
        }

        sb.Append(" { ");
        for (var i = 0; i < plan.Targets.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var target = plan.Targets[i];
            sb.Append(target.PropertyName).Append(" = ").Append(paramNames[target]);
        }

        sb.Append(" }");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

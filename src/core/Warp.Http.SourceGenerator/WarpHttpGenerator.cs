using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Warp.Http.SourceGenerator.Emitters;

namespace Warp.Http.SourceGenerator;

[Generator]
public sealed class WarpHttpGenerator : IIncrementalGenerator
{
    private const string WarpHttpAttributeMetadataName = "Warp.Http.WarpHttpAttribute";
    private const string IRequestHandlerMetadataName = "Warp.Core.Handlers.IRequestHandler`2";
    private const string IStreamRequestHandlerMetadataName = "Warp.Core.Handlers.IStreamRequestHandler`2";
    private const string IJobMetadataName = "Warp.Core.Handlers.IJob";
    private const string IMessageMetadataName = "Warp.Core.Handlers.IMessage";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsCandidate(node),
                transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol)
            .Where(static symbol => symbol is not null);

        var combined = context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(combined, static (spc, source) => Execute(spc, source.Left, source.Right!));
    }

    private static bool IsCandidate(SyntaxNode node)
    {
        return node switch
        {
            ClassDeclarationSyntax c => c.AttributeLists.Count > 0,
            RecordDeclarationSyntax r => r.AttributeLists.Count > 0,
            _ => false,
        };
    }

    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<INamedTypeSymbol?> candidates)
    {
        if (string.Equals(compilation.AssemblyName, "Warp.Http", StringComparison.Ordinal))
        {
            return;
        }

        var warpHttpAttributeSymbol = compilation.GetTypeByMetadataName(WarpHttpAttributeMetadataName);
        if (warpHttpAttributeSymbol is null)
        {
            return;
        }

        var iRequestHandlerSymbol = compilation.GetTypeByMetadataName(IRequestHandlerMetadataName);
        var iStreamRequestHandlerSymbol = compilation.GetTypeByMetadataName(IStreamRequestHandlerMetadataName);
        var iJobSymbol = compilation.GetTypeByMetadataName(IJobMetadataName);
        var iMessageSymbol = compilation.GetTypeByMetadataName(IMessageMetadataName);

        if (iRequestHandlerSymbol is null && iStreamRequestHandlerSymbol is null)
        {
            return;
        }

        var endpoints = new List<(HttpEndpointModel Model, BindingPlan Plan)>();

        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate.IsAbstract)
            {
                continue;
            }

            try
            {
                ProcessCandidate(
                    context,
                    compilation,
                    candidate,
                    warpHttpAttributeSymbol,
                    iRequestHandlerSymbol,
                    iStreamRequestHandlerSymbol,
                    iJobSymbol,
                    iMessageSymbol,
                    endpoints);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ReportInternalError(context, candidate, ex);
            }
        }

        if (endpoints.Count == 0)
        {
            return;
        }

        var assemblySafe = SanitizeIdentifier(compilation.AssemblyName ?? "Unknown");
        var source = RegistryEmitter.Emit(assemblySafe, endpoints);
        context.AddSource("WarpHttpRegistry.g.cs", source);
    }

    private static void ProcessCandidate(
        SourceProductionContext context,
        Compilation compilation,
        INamedTypeSymbol candidate,
        INamedTypeSymbol warpHttpAttributeSymbol,
        INamedTypeSymbol? iRequestHandlerSymbol,
        INamedTypeSymbol? iStreamRequestHandlerSymbol,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol,
        List<(HttpEndpointModel Model, BindingPlan Plan)> endpoints)
    {
        var httpAttributes = candidate.GetAttributes()
            .Where(a => InheritsFrom(a.AttributeClass, warpHttpAttributeSymbol))
            .ToArray();

        if (httpAttributes.Length == 0)
        {
            return;
        }

        var classification = ClassifyHandler(candidate, iRequestHandlerSymbol, iStreamRequestHandlerSymbol);
        if (classification is null)
        {
            ReportInvalidHandler(
                context,
                candidate,
                "must implement IRequestHandler<TRequest, TResponse> or IStreamRequestHandler<TRequest, TResponse>");
            return;
        }

        var requestType = classification.Value.RequestType;
        if (RequestImplementsBackgroundWorkInterface(requestType, iJobSymbol, iMessageSymbol))
        {
            ReportInvalidHandler(
                context,
                candidate,
                $"request type '{requestType.ToDisplayString()}' implements IJob or IMessage and cannot be HTTP-exposed (write a thin IRequest<Guid> wrapper that calls IPublisher.Enqueue instead)");
            return;
        }

        if (httpAttributes.Length > 1)
        {
            var anyMissingName = httpAttributes.Any(a => ReadNamedArg(a, "Name") is null);
            if (anyMissingName)
            {
                var location = candidate.Locations.FirstOrDefault() ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MissingNameOnMultiAttribute,
                    location,
                    candidate.ToDisplayString()));
            }
        }

        foreach (var attr in httpAttributes)
        {
            var (method, route) = ReadRouteAndMethod(attr);
            if (method is null || route is null)
            {
                continue;
            }

            var group = ReadNamedArg(attr, "Group") as string;
            var name = ReadNamedArg(attr, "Name") as string;

            var plan = Emitters.BindingEmitter.Build(compilation, requestType, method);

            // Mixed shape with more than one body-bound target is unsupported — Minimal API
            // accepts at most one body parameter, and emitting the lambda would throw
            // KeyNotFoundException because EmitMixedHandler captures only the first body
            // target. Diagnose explicitly so the rest of the assembly's endpoints still emit.
            if (plan.Shape == BindingShape.Mixed
                && plan.Targets.Count(t => t.Source == BindingSource.Body) > 1)
            {
                ReportMultipleBodyTargets(context, candidate);
                continue;
            }

            // A multipart-form request and a JSON [FromBody] parameter both consume the request
            // body — Minimal API reads it once, so the two can't coexist on one endpoint.
            if (plan.HasFormTargets && plan.HasBodyTargets)
            {
                ReportFormAndBodyConflict(context, candidate);
                continue;
            }

            ReportRequiredScalarDefaults(context, plan);

            var model = new HttpEndpointModel(
                handlerType: candidate,
                requestType: requestType,
                responseType: classification.Value.ResponseType,
                kind: classification.Value.Kind,
                method: method,
                route: route,
                group: group,
                name: name,
                requiresFormBinding: plan.HasFormTargets);

            endpoints.Add((model, plan));
        }
    }

    private static (HttpHandlerKind Kind, INamedTypeSymbol RequestType, ITypeSymbol ResponseType)? ClassifyHandler(
        INamedTypeSymbol handlerCandidate,
        INamedTypeSymbol? iRequestHandlerSymbol,
        INamedTypeSymbol? iStreamRequestHandlerSymbol)
    {
        // Stream handler is more specific — check first so we don't accidentally classify
        // a stream handler as a regular request handler.
        if (iStreamRequestHandlerSymbol is not null)
        {
            var streamIface = handlerCandidate.AllInterfaces.FirstOrDefault(i =>
                i.OriginalDefinition.Equals(iStreamRequestHandlerSymbol, SymbolEqualityComparer.Default));
            if (streamIface is not null && streamIface.TypeArguments[0] is INamedTypeSymbol streamRequest)
            {
                return (HttpHandlerKind.Stream, streamRequest, streamIface.TypeArguments[1]);
            }
        }

        if (iRequestHandlerSymbol is not null)
        {
            var requestIface = handlerCandidate.AllInterfaces.FirstOrDefault(i =>
                i.OriginalDefinition.Equals(iRequestHandlerSymbol, SymbolEqualityComparer.Default));
            if (requestIface is not null && requestIface.TypeArguments[0] is INamedTypeSymbol request)
            {
                return (HttpHandlerKind.Request, request, requestIface.TypeArguments[1]);
            }
        }

        return null;
    }

    private static bool RequestImplementsBackgroundWorkInterface(
        INamedTypeSymbol requestType,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol)
    {
        return requestType.AllInterfaces.Any(i =>
            (iJobSymbol is not null && i.Equals(iJobSymbol, SymbolEqualityComparer.Default))
            || (iMessageSymbol is not null && i.Equals(iMessageSymbol, SymbolEqualityComparer.Default)));
    }

    private static bool InheritsFrom(INamedTypeSymbol? type, INamedTypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.Equals(baseType, SymbolEqualityComparer.Default))
            {
                return true;
            }
        }

        return false;
    }

    private static (string? Method, string? Route) ReadRouteAndMethod(AttributeData attr)
    {
        var ctor = attr.ConstructorArguments;

        if (ctor.Length == 2)
        {
            return (ctor[0].Value as string, ctor[1].Value as string);
        }

        if (ctor.Length == 1)
        {
            var route = ctor[0].Value as string;
            var method = MethodFromAttributeName(attr.AttributeClass?.Name);
            return (method, route);
        }

        return (null, null);
    }

    private static string? MethodFromAttributeName(string? name)
    {
        return name switch
        {
            "WarpHttpGetAttribute" => "GET",
            "WarpHttpPostAttribute" => "POST",
            "WarpHttpPutAttribute" => "PUT",
            "WarpHttpPatchAttribute" => "PATCH",
            "WarpHttpDeleteAttribute" => "DELETE",
            _ => null,
        };
    }

    private static object? ReadNamedArg(AttributeData attr, string name)
    {
        return attr.NamedArguments
            .Where(p => string.Equals(p.Key, name, StringComparison.Ordinal))
            .Select(p => p.Value.Value)
            .FirstOrDefault();
    }

    private static void ReportInvalidHandler(SourceProductionContext context, INamedTypeSymbol type, string reason)
    {
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.InvalidHandler,
            location,
            type.ToDisplayString(),
            reason));
    }

    // WHTTP005: only the [AsParameters] shape (non-body verbs, no [FromBody] member) is affected —
    // whole-body and mixed shapes deserialize the body via JSON, which honors C# defaults for
    // omitted members. Within AsParameters, a query-bound non-nullable value type with a C#
    // default still binds as a required query parameter, silently turning the default into a 400.
    private static void ReportRequiredScalarDefaults(SourceProductionContext context, BindingPlan plan)
    {
        if (plan.Shape != BindingShape.AsParameters)
        {
            return;
        }

        foreach (var target in plan.Targets)
        {
            if (target.Source != BindingSource.Query)
            {
                continue;
            }

            if (!target.HasClrDefault || !IsNonNullableValueType(target.Type))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.RequiredScalarWithIgnoredDefault,
                target.Location ?? Location.None,
                target.MemberName));
        }
    }

    private static bool IsNonNullableValueType(ITypeSymbol type)
    {
        return type.IsValueType
            && type.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T;
    }

    private static void ReportMultipleBodyTargets(SourceProductionContext context, INamedTypeSymbol type)
    {
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.MultipleBodyTargets,
            location,
            type.ToDisplayString()));
    }

    private static void ReportFormAndBodyConflict(SourceProductionContext context, INamedTypeSymbol type)
    {
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.FormAndBodyConflict,
            location,
            type.ToDisplayString()));
    }

    private static void ReportInternalError(SourceProductionContext context, INamedTypeSymbol type, Exception ex)
    {
        var location = type.Locations.FirstOrDefault() ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            Diagnostics.InternalGeneratorError,
            location,
            type.ToDisplayString(),
            ex.GetType().Name + ": " + ex.Message));
    }

    private static string SanitizeIdentifier(string raw)
    {
        var chars = raw.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]))
            {
                chars[i] = '_';
            }
        }

        if (chars.Length == 0 || char.IsDigit(chars[0]))
        {
            return "A_" + new string(chars);
        }

        return new string(chars);
    }
}

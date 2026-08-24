using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Warp.SourceGenerator;

/// <summary>
/// Compile-time half of the addon policy-axis validation: everything decidable from types alone is
/// reported as a build error here, so a misplaced policy attribute fails the build instead of the first
/// <c>AddWarp</c> call — with a squiggle on the attribute rather than a type name inside a startup
/// stack trace.
/// <para>
/// The runtime check (<c>ServiceConfiguration.ValidateAddonAttributesOnHandlers</c>) is NOT replaced.
/// It remains the backstop for two shapes this pass structurally cannot see — a handler declared in a
/// referenced assembly that was built without this analyzer and hand-registered, and Warp.Core itself
/// (which <c>WarpMediatorGenerator</c> skips wholesale) — and it owns the one rule that is not a
/// type-level fact: a handler <c>[Timeout]</c> under a Total-scoped GLOBAL default, which is only
/// knowable by invoking the <c>AddTimeout</c> options lambda.
/// </para>
/// </summary>
internal static class PolicyAxisValidator
{
    private const string MutexMetadataName = "Warp.Core.Concurrency.MutexAttribute";
    private const string SemaphoreMetadataName = "Warp.Core.Concurrency.SemaphoreAttribute";
    private const string RateLimitMetadataName = "Warp.Core.RateLimit.RateLimitAttribute";
    private const string TimeoutMetadataName = "Warp.Core.Timeout.TimeoutAttribute";
    private const string RetryMetadataName = "Warp.Core.Handlers.RetryAttribute";
    private const string CircuitBreakerMetadataName = "Warp.Core.CircuitBreaker.CircuitBreakerAttribute";
    private const string TimeoutScopeMetadataName = "Warp.Core.Timeout.TimeoutScope";

    public static void Validate(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol?> candidates,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol,
        INamedTypeSymbol? iJobHandlerSymbol,
        INamedTypeSymbol? iMessageHandlerSymbol,
        INamedTypeSymbol? iRequestHandlerSymbol,
        INamedTypeSymbol? iStreamRequestHandlerSymbol)
    {
        var families = BuildFamilies(compilation);
        if (families.Count == 0)
        {
            return;
        }

        var timeoutAttributeSymbol = compilation.GetTypeByMetadataName(TimeoutMetadataName);
        var totalScopeValue = GetTotalScopeValue(compilation);

        var pairs = EnumerateHandlerPairs(
            candidates,
            compilation,
            iJobSymbol,
            iMessageSymbol,
            iJobHandlerSymbol,
            iMessageHandlerSymbol,
            iRequestHandlerSymbol,
            iStreamRequestHandlerSymbol);

        foreach (var pair in pairs)
        {
            // Self-handling job (`class Foo : IJob, IJobHandler<Foo>`): the handler IS the contract, so
            // there is only one axis and nothing to shadow. Exempt, exactly as the runtime check is.
            if (SymbolEqualityComparer.Default.Equals(pair.Handler, pair.Request))
            {
                continue;
            }

            foreach (var family in families)
            {
                var declared = FindAttribute(pair.Handler, family);
                if (declared == null)
                {
                    continue;
                }

                if (!pair.HandlerAxisSupported)
                {
                    if (!family.RejectedOnUnsupportedShapes)
                    {
                        continue;
                    }

                    Report(
                        context,
                        Diagnostics.PolicyOnUnsupportedHandler,
                        declared,
                        pair.Handler,
                        ShortAttributeName(declared),
                        pair.Handler.Name,
                        pair.Request.Name);

                    continue;
                }

                if (FindAttribute(pair.Request, family) != null)
                {
                    Report(
                        context,
                        Diagnostics.PolicyOnBothAxes,
                        declared,
                        pair.Handler,
                        family.Name,
                        pair.Request.Name,
                        pair.Handler.Name);

                    continue;
                }

                if (timeoutAttributeSymbol != null
                    && SymbolEqualityComparer.Default.Equals(declared.AttributeClass, timeoutAttributeSymbol)
                    && IsTotalScope(declared, totalScopeValue))
                {
                    Report(context, Diagnostics.TotalTimeoutOnHandler, declared, pair.Handler, pair.Handler.Name);
                }
            }
        }
    }

    private static IEnumerable<HandlerPair> EnumerateHandlerPairs(
        ImmutableArray<INamedTypeSymbol?> candidates,
        Compilation compilation,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol,
        INamedTypeSymbol? iJobHandlerSymbol,
        INamedTypeSymbol? iMessageHandlerSymbol,
        INamedTypeSymbol? iRequestHandlerSymbol,
        INamedTypeSymbol? iStreamRequestHandlerSymbol)
    {
        // A type can implement several handler interfaces; each (handler, contract) pair is judged on its
        // own, but the same pair reached twice (IJobHandler<T> plus an explicit IRequestHandler<T, Unit>)
        // must not report the same conflict twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(candidate.ContainingAssembly, compilation.Assembly))
            {
                continue;
            }

            foreach (var iface in candidate.AllInterfaces)
            {
                var definition = iface.OriginalDefinition;
                bool supported;

                if (Matches(definition, iJobHandlerSymbol) || Matches(definition, iMessageHandlerSymbol))
                {
                    supported = true;
                }
                else if (Matches(definition, iStreamRequestHandlerSymbol))
                {
                    supported = false;
                }
                else if (Matches(definition, iRequestHandlerSymbol))
                {
                    // An in-memory request has no job row to reschedule or delete, so the policy
                    // behaviours early-return for it. Only an IJob/IMessage contract dispatched through
                    // IRequestHandler<,> can honour a handler-declared policy.
                    supported = Implements(iface.TypeArguments[0], iJobSymbol) || Implements(iface.TypeArguments[0], iMessageSymbol);
                }
                else
                {
                    continue;
                }

                if (iface.TypeArguments[0] is not INamedTypeSymbol request)
                {
                    continue;
                }

                if (!seen.Add(candidate.ToDisplayString() + "|" + request.ToDisplayString()))
                {
                    continue;
                }

                yield return new HandlerPair(candidate, request, supported);
            }
        }
    }

    private static List<PolicyFamily> BuildFamilies(Compilation compilation)
    {
        var families = new List<PolicyFamily>();

        // Families, not attribute types: every attribute in a family writes the same metadata slot, so a
        // contract [Mutex] shadows a handler [Semaphore] just as surely as another [Mutex] would.
        // RejectedOnUnsupportedShapes mirrors the runtime table — Retry and CircuitBreaker have always
        // been tolerated (as dead code) on non-job handlers, and rejecting them there now would be an
        // unspecced breaking change.
        Add(families, compilation, "Mutex/Semaphore", rejectedOnUnsupportedShapes: true, MutexMetadataName, SemaphoreMetadataName);
        Add(families, compilation, "RateLimit", rejectedOnUnsupportedShapes: true, RateLimitMetadataName);
        Add(families, compilation, "Timeout", rejectedOnUnsupportedShapes: true, TimeoutMetadataName);
        Add(families, compilation, "Retry", rejectedOnUnsupportedShapes: false, RetryMetadataName);
        Add(families, compilation, "CircuitBreaker", rejectedOnUnsupportedShapes: false, CircuitBreakerMetadataName);

        return families;
    }

    private static void Add(
        List<PolicyFamily> families,
        Compilation compilation,
        string name,
        bool rejectedOnUnsupportedShapes,
        params string[] metadataNames)
    {
        var symbols = metadataNames
            .Select(compilation.GetTypeByMetadataName)
            .Where(x => x != null)
            .ToImmutableArray();

        if (symbols.Length == 0)
        {
            return;
        }

        families.Add(new PolicyFamily(name, symbols!, rejectedOnUnsupportedShapes));
    }

    private static AttributeData? FindAttribute(INamedTypeSymbol type, PolicyFamily family) =>
        type.GetAttributes()
            .FirstOrDefault(x => family.AttributeTypes.Any(y => SymbolEqualityComparer.Default.Equals(x.AttributeClass, y)));

    private static bool IsTotalScope(AttributeData attribute, int? totalScopeValue)
    {
        if (totalScopeValue == null)
        {
            return false;
        }

        return attribute.NamedArguments.Any(x =>
            string.Equals(x.Key, "Scope", StringComparison.Ordinal)
            && x.Value.Value is int value
            && value == totalScopeValue.Value);
    }

    private static int? GetTotalScopeValue(Compilation compilation)
    {
        // Read the member's value rather than hard-coding 2, so renumbering the enum can't silently
        // turn this check into a no-op.
        var scope = compilation.GetTypeByMetadataName(TimeoutScopeMetadataName);

        return scope?.GetMembers("Total")
            .OfType<IFieldSymbol>()
            .Select(x => x.ConstantValue)
            .OfType<int>()
            .Cast<int?>()
            .FirstOrDefault();
    }

    private static void Report(
        SourceProductionContext context,
        DiagnosticDescriptor descriptor,
        AttributeData attribute,
        INamedTypeSymbol handler,
        params object?[] messageArgs)
    {
        // Point at the attribute the author wrote; fall back to the handler declaration when the
        // attribute came from metadata (a partial declared elsewhere) and has no syntax reference.
        var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
            ?? handler.Locations.FirstOrDefault()
            ?? Location.None;

        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, messageArgs));
    }

    private static string ShortAttributeName(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.Name ?? "Policy";

        return name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name.Substring(0, name.Length - "Attribute".Length)
            : name;
    }

    private static bool Matches(INamedTypeSymbol definition, INamedTypeSymbol? handlerInterface) =>
        handlerInterface is not null && definition.Equals(handlerInterface, SymbolEqualityComparer.Default);

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol? marker) =>
        marker is not null && type.AllInterfaces.Any(x => x.Equals(marker, SymbolEqualityComparer.Default));

    private sealed class PolicyFamily
    {
        public PolicyFamily(string name, ImmutableArray<INamedTypeSymbol> attributeTypes, bool rejectedOnUnsupportedShapes)
        {
            Name = name;
            AttributeTypes = attributeTypes;
            RejectedOnUnsupportedShapes = rejectedOnUnsupportedShapes;
        }

        public string Name { get; }

        public ImmutableArray<INamedTypeSymbol> AttributeTypes { get; }

        public bool RejectedOnUnsupportedShapes { get; }
    }

    private sealed class HandlerPair
    {
        public HandlerPair(INamedTypeSymbol handler, INamedTypeSymbol request, bool handlerAxisSupported)
        {
            Handler = handler;
            Request = request;
            HandlerAxisSupported = handlerAxisSupported;
        }

        public INamedTypeSymbol Handler { get; }

        public INamedTypeSymbol Request { get; }

        public bool HandlerAxisSupported { get; }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Warp.SourceGenerator;

/// <summary>
/// Compile-time half of the addon policy-axis check (§8.8) — placements no execution path can honour.
/// Handlers outside this compilation are left to <c>PolicyResolver</c> at execution.
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

        // Judged per HANDLER, not per pair: the attribute is honoured if ANY of the handler's interfaces
        // supports the axis, and grouping also collapses duplicate reports on the one attribute syntax.
        foreach (var group in GroupByHandler(pairs))
        {
            var handler = group[0].Handler;
            var anySupported = group.Exists(x => x.HandlerAxisSupported);

            foreach (var family in families)
            {
                var declared = FindAttribute(handler, family);
                if (declared == null)
                {
                    continue;
                }

                if (!anySupported)
                {
                    if (!family.RejectedOnUnsupportedShapes)
                    {
                        continue;
                    }

                    Report(
                        context,
                        Diagnostics.PolicyOnUnsupportedHandler,
                        declared,
                        handler,
                        ShortAttributeName(declared),
                        handler.Name,
                        group[0].Request.Name);

                    continue;
                }

                if (timeoutAttributeSymbol != null
                    && SymbolEqualityComparer.Default.Equals(declared.AttributeClass, timeoutAttributeSymbol)
                    && IsTotalScope(declared, totalScopeValue))
                {
                    Report(context, Diagnostics.TotalTimeoutOnHandler, declared, handler, handler.Name);
                }
            }
        }
    }

    // `order` keeps emission deterministic across runs.
    private static List<List<HandlerPair>> GroupByHandler(IEnumerable<HandlerPair> pairs)
    {
        var byHandler = new Dictionary<string, List<HandlerPair>>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var pair in pairs)
        {
            // Self-handling job: the declaration is on the contract, which happens to be the handler too.
            if (SymbolEqualityComparer.Default.Equals(pair.Handler, pair.Request))
            {
                continue;
            }

            var key = pair.Handler.ToDisplayString();
            if (!byHandler.TryGetValue(key, out var bucket))
            {
                bucket = [];
                byHandler.Add(key, bucket);
                order.Add(key);
            }

            bucket.Add(pair);
        }

        return order.ConvertAll(x => byHandler[x]);
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
        // The same pair reached through two interfaces must not report twice.
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
                    // An in-memory request has no job row to reschedule, so the behaviours early-return.
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

        // Only the four attributes #242 covered fail loudly on unsupported shapes — Retry and the breaker
        // have always been tolerated (dead) there.
        Add(families, compilation, rejectedOnUnsupportedShapes: true, MutexMetadataName, SemaphoreMetadataName);
        Add(families, compilation, rejectedOnUnsupportedShapes: true, RateLimitMetadataName);
        Add(families, compilation, rejectedOnUnsupportedShapes: true, TimeoutMetadataName);
        Add(families, compilation, rejectedOnUnsupportedShapes: false, RetryMetadataName);
        Add(families, compilation, rejectedOnUnsupportedShapes: false, CircuitBreakerMetadataName);

        return families;
    }

    private static void Add(
        List<PolicyFamily> families,
        Compilation compilation,
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

        families.Add(new PolicyFamily(symbols!, rejectedOnUnsupportedShapes));
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
        // Read the member rather than hard-coding 2: renumbering must not silently no-op the check.
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
        // Point at the attribute the author wrote; fall back when it has no syntax reference.
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
        public PolicyFamily(ImmutableArray<INamedTypeSymbol> attributeTypes, bool rejectedOnUnsupportedShapes)
        {
            AttributeTypes = attributeTypes;
            RejectedOnUnsupportedShapes = rejectedOnUnsupportedShapes;
        }

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

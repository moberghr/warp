using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Warp.SourceGenerator;

/// <summary>
/// Reports WARP003: an <c>IJob</c> / <c>IMessage</c> contract nothing handles. The generator already
/// walks every contract and every handler beside it to build the dispatch map — a contract missing
/// from that map is a build-time fact, and §8.8's criterion for a diagnostic ("what no execution path
/// can honour") covers it: publishing succeeds and the job fails on a worker instead.
/// </summary>
/// <remarks>
/// Warning, never error. The compilation cannot see an assembly that references it, so a contract
/// handled downstream is indistinguishable from one handled nowhere. Two things keep that from being
/// noisy: a compilation declaring no job-family handler at all is a contracts assembly and is skipped
/// entirely, and what survives is suppressible per type.
/// </remarks>
internal static class UnhandledJobValidator
{
    private const string ISagaHandlerMetadataName = "Warp.Core.Sagas.ISagaHandler`2";

    public static void Validate(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol?> candidates,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol,
        INamedTypeSymbol? iJobHandlerSymbol,
        INamedTypeSymbol? iMessageHandlerSymbol,
        Dictionary<string, INamedTypeSymbol> jobHandlerMap,
        Dictionary<string, List<INamedTypeSymbol>> messageHandlerMap)
    {
        if (iJobSymbol is null && iMessageSymbol is null)
        {
            return;
        }

        var iSagaHandlerSymbol = compilation.GetTypeByMetadataName(ISagaHandlerMetadataName);

        var declaresHandler = false;
        var unhandled = new List<Unhandled>();

        // A partial contract reaches the syntax provider once per declaration, and the two entries
        // carry different locations — Roslyn would not collapse them.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!IsLocalConcreteType(candidate, compilation))
            {
                continue;
            }

            if (DeclaresJobFamilyHandler(candidate!, iJobHandlerSymbol, iMessageHandlerSymbol, iSagaHandlerSymbol))
            {
                declaresHandler = true;
            }

            var contract = ClassifyContract(candidate!, iJobSymbol, iMessageSymbol);
            if (contract is null)
            {
                continue;
            }

            var fullName = candidate!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (jobHandlerMap.ContainsKey(fullName) || messageHandlerMap.ContainsKey(fullName))
            {
                continue;
            }

            if (!seen.Add(fullName))
            {
                continue;
            }

            unhandled.Add(new Unhandled(candidate, fullName, contract));
        }

        // A compilation with no job-family handler of its own is a contracts assembly: every contract in
        // it is handled by something downstream that this compilation cannot see.
        if (!declaresHandler || unhandled.Count == 0)
        {
            return;
        }

        // Only walked once something would otherwise be reported — it is a full scan of the compilation
        // and every reference, and the clean case must not pay for it.
        var sagaHandled = CollectSagaHandledMessages(compilation, iSagaHandlerSymbol);

        foreach (var entry in unhandled)
        {
            if (sagaHandled.Contains(entry.FullName))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.UnhandledJobType,
                entry.Contract.Locations.Length > 0 ? entry.Contract.Locations[0] : Location.None,
                entry.Contract.Name,
                entry.Kind.MarkerName,
                entry.Kind.HandlerName));
        }
    }

    private static bool IsLocalConcreteType(INamedTypeSymbol? candidate, Compilation compilation)
    {
        if (candidate is null || candidate.IsAbstract || candidate.TypeKind == TypeKind.Interface)
        {
            return false;
        }

        // An open generic contract cannot be matched against the handler map by name with any
        // confidence — the map keys a closed type, the declaration keys a type parameter.
        if (candidate.IsGenericType)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(candidate.ContainingAssembly, compilation.Assembly);
    }

    private static bool DeclaresJobFamilyHandler(
        INamedTypeSymbol candidate,
        INamedTypeSymbol? iJobHandlerSymbol,
        INamedTypeSymbol? iMessageHandlerSymbol,
        INamedTypeSymbol? iSagaHandlerSymbol)
    {
        return candidate.AllInterfaces.Any(x =>
            Matches(x.OriginalDefinition, iJobHandlerSymbol)
            || Matches(x.OriginalDefinition, iMessageHandlerSymbol)
            || Matches(x.OriginalDefinition, iSagaHandlerSymbol));
    }

    private static ContractKind? ClassifyContract(
        INamedTypeSymbol candidate,
        INamedTypeSymbol? iJobSymbol,
        INamedTypeSymbol? iMessageSymbol)
    {
        // Job first, mirroring the emitter: a type implementing both is dispatched as a job.
        if (Implements(candidate, iJobSymbol))
        {
            return ContractKind.Job;
        }

        if (Implements(candidate, iMessageSymbol))
        {
            return ContractKind.Message;
        }

        return null;
    }

    private static bool Implements(INamedTypeSymbol candidate, INamedTypeSymbol? marker) =>
        marker is not null && candidate.AllInterfaces.Any(x => x.Equals(marker, SymbolEqualityComparer.Default));

    /// <summary>
    /// Saga messages are handled through <c>ISagaHandler&lt;TSaga, TMessage&gt;</c>, which
    /// <c>AddSagaHandler</c> registers as a generated <c>SagaHandlerProxy</c>. The user's handler never
    /// implements <c>IMessageHandler&lt;T&gt;</c> itself, so it is absent from the dispatch map and the
    /// message would otherwise read as unhandled.
    /// </summary>
    private static HashSet<string> CollectSagaHandledMessages(
        Compilation compilation,
        INamedTypeSymbol? iSagaHandlerSymbol)
    {
        var handled = new HashSet<string>(StringComparer.Ordinal);
        if (iSagaHandlerSymbol is null)
        {
            return handled;
        }

        foreach (var type in WarpMediatorGenerator.GetAllTypes(compilation))
        {
            if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!Matches(iface.OriginalDefinition, iSagaHandlerSymbol))
                {
                    continue;
                }

                handled.Add(iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }

        return handled;
    }

    private static bool Matches(INamedTypeSymbol definition, INamedTypeSymbol? handlerInterface) =>
        handlerInterface is not null && definition.Equals(handlerInterface, SymbolEqualityComparer.Default);

    private sealed class ContractKind
    {
        public static readonly ContractKind Job = new("IJob", "IJobHandler");
        public static readonly ContractKind Message = new("IMessage", "IMessageHandler");

        private ContractKind(string markerName, string handlerName)
        {
            MarkerName = markerName;
            HandlerName = handlerName;
        }

        public string MarkerName { get; }

        public string HandlerName { get; }
    }

    private sealed class Unhandled
    {
        public Unhandled(INamedTypeSymbol contract, string fullName, ContractKind kind)
        {
            Contract = contract;
            FullName = fullName;
            Kind = kind;
        }

        public INamedTypeSymbol Contract { get; }

        public string FullName { get; }

        public ContractKind Kind { get; }
    }
}

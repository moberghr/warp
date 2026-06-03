using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Warp.SourceGenerator;

[Generator]
public sealed class WarpMediatorGenerator : IIncrementalGenerator
{
    private const string IRequestMetadataName = "Warp.Core.Handlers.IRequest`1";
    private const string IJobMetadataName = "Warp.Core.Handlers.IJob";
    private const string IMessageMetadataName = "Warp.Core.Handlers.IMessage";
    private const string IRequestHandlerMetadataName = "Warp.Core.Handlers.IRequestHandler`2";
    private const string IJobHandlerMetadataName = "Warp.Core.Handlers.IJobHandler`1";
    private const string IMessageHandlerMetadataName = "Warp.Core.Handlers.IMessageHandler`1";
    private const string IStreamRequestMetadataName = "Warp.Core.Handlers.IStreamRequest`1";
    private const string IStreamRequestHandlerMetadataName = "Warp.Core.Handlers.IStreamRequestHandler`2";
    private const string IPublishPipelineBehaviorMetadataName = "Warp.Core.Handlers.IPublishPipelineBehavior`1";
    private const string IPipelineBehaviorMetadataName = "Warp.Core.Handlers.IPipelineBehavior`2";
    private const string IStreamPipelineBehaviorMetadataName = "Warp.Core.Handlers.IStreamPipelineBehavior`2";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var classDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                transform: static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol)
            .Where(static symbol => symbol is not null);

        var compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(spc, source.Left, source.Right!));
    }

    private static void Execute(
        SourceProductionContext context,
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol?> candidates)
    {
        // Warp.Core itself has no user-level handlers. Its own open-generic pipeline/publish
        // behaviors (MutexPipelineBehavior, RetryPublishBehavior, ...) are registered by opt-in
        // addon methods (AddMutex, AddRetry, ...) — auto-registering them here would short-circuit
        // that opt-in. Skip source-generation for Core entirely.
        if (string.Equals(compilation.AssemblyName, "Warp.Core", StringComparison.Ordinal))
        {
            return;
        }

        var iRequestSymbol = compilation.GetTypeByMetadataName(IRequestMetadataName);
        var iJobSymbol = compilation.GetTypeByMetadataName(IJobMetadataName);
        var iMessageSymbol = compilation.GetTypeByMetadataName(IMessageMetadataName);
        var iRequestHandlerSymbol = compilation.GetTypeByMetadataName(IRequestHandlerMetadataName);
        var iJobHandlerSymbol = compilation.GetTypeByMetadataName(IJobHandlerMetadataName);
        var iMessageHandlerSymbol = compilation.GetTypeByMetadataName(IMessageHandlerMetadataName);

        var iStreamRequestSymbol = compilation.GetTypeByMetadataName(IStreamRequestMetadataName);
        var iStreamRequestHandlerSymbol = compilation.GetTypeByMetadataName(IStreamRequestHandlerMetadataName);

        if (iRequestSymbol is null || iRequestHandlerSymbol is null)
        {
            return;
        }

        var allHandlerMap = BuildHandlerMap(compilation, iRequestHandlerSymbol);
        var streamHandlerMap = iStreamRequestHandlerSymbol is not null
            ? BuildHandlerMap(compilation, iStreamRequestHandlerSymbol)
            : [];
        var jobHandlerMap = iJobHandlerSymbol is not null
            ? BuildSingleTypeHandlerMap(compilation, iJobHandlerSymbol)
            : [];
        var messageHandlerMap = iMessageHandlerSymbol is not null
            ? BuildSingleTypeMultiHandlerMap(compilation, iMessageHandlerSymbol)
            : [];

        var iPublishBehaviorSymbol = compilation.GetTypeByMetadataName(IPublishPipelineBehaviorMetadataName);

        var requestTypes = new List<RequestTypeInfo>();
        var streamRequestTypes = new List<StreamRequestTypeInfo>();
        var jobTypes = new List<JobTypeInfo>();

        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate.IsAbstract)
            {
                continue;
            }

            // Check if this type implements IRequest<T>
            INamedTypeSymbol? requestInterface = null;
#pragma warning disable S3267 // Loop with break is clearer than LINQ for early-exit pattern matching
            foreach (var iface in candidate.AllInterfaces)
#pragma warning restore S3267
            {
                if (iface.OriginalDefinition.Equals(iRequestSymbol, SymbolEqualityComparer.Default))
                {
                    requestInterface = iface;
                    break;
                }
            }

            var candidateFullName = GetFullyQualifiedName(candidate);

            if (requestInterface is not null)
            {
                // Check if it's an IJob type
                var isJob = iJobSymbol is not null && candidate.AllInterfaces.Any(i =>
                    i.Equals(iJobSymbol, SymbolEqualityComparer.Default));

                // Check if it's an IMessage type
                var isMessage = iMessageSymbol is not null && candidate.AllInterfaces.Any(i =>
                    i.Equals(iMessageSymbol, SymbolEqualityComparer.Default));

                if (isJob || isMessage)
                {
                    if (isJob && jobHandlerMap.TryGetValue(candidateFullName, out var jobHandlerSymbol))
                    {
                        var methodName = "Execute_" + candidate.Name;
                        jobTypes.Add(new JobTypeInfo(
                            candidateFullName,
                            [GetFullyQualifiedName(jobHandlerSymbol)],
                            methodName,
                            isMessage: false));
                    }
                    else if (isMessage && messageHandlerMap.TryGetValue(candidateFullName, out var messageHandlers))
                    {
                        var methodName = "Execute_" + candidate.Name;
                        var handlerNames = new List<string>(messageHandlers.Count);
                        foreach (var h in messageHandlers)
                        {
                            handlerNames.Add(GetFullyQualifiedName(h));
                        }

                        jobTypes.Add(new JobTypeInfo(
                            candidateFullName,
                            handlerNames,
                            methodName,
                            isMessage: true));
                    }

                    continue;
                }

                // Check if it's an IStreamRequest type — handled in the stream branch below
                var isStream = iStreamRequestSymbol is not null && candidate.AllInterfaces.Any(i =>
                    i.OriginalDefinition.Equals(iStreamRequestSymbol, SymbolEqualityComparer.Default));

                if (!isStream)
                {
                    // It's a plain IRequest<TResponse> — mediator path
                    var responseType = requestInterface.TypeArguments[0];
                    var responseFullName = GetFullyQualifiedName(responseType);

                    var handlerKey = candidateFullName + "|" + responseFullName;
                    if (allHandlerMap.TryGetValue(handlerKey, out var reqHandlerSymbol))
                    {
                        var wrapperFieldName = "_wrapper_" + candidate.Name;
                        requestTypes.Add(new RequestTypeInfo(
                            candidateFullName,
                            responseFullName,
                            GetFullyQualifiedName(reqHandlerSymbol),
                            wrapperFieldName,
                            candidate.DeclaredAccessibility));
                    }

                    continue;
                }
            }

            // Check if this type implements IStreamRequest<T>
            if (iStreamRequestSymbol is not null)
            {
                INamedTypeSymbol? streamRequestInterface = null;
#pragma warning disable S3267
                foreach (var iface in candidate.AllInterfaces)
#pragma warning restore S3267
                {
                    if (iface.OriginalDefinition.Equals(iStreamRequestSymbol, SymbolEqualityComparer.Default))
                    {
                        streamRequestInterface = iface;
                        break;
                    }
                }

                if (streamRequestInterface is not null)
                {
                    var streamResponseType = streamRequestInterface.TypeArguments[0];
                    var streamResponseFullName = GetFullyQualifiedName(streamResponseType);

                    var streamHandlerKey = candidateFullName + "|" + streamResponseFullName;
                    if (streamHandlerMap.TryGetValue(streamHandlerKey, out var streamHandlerSymbol))
                    {
                        var wrapperFieldName = "_streamWrapper_" + candidate.Name;
                        streamRequestTypes.Add(new StreamRequestTypeInfo(
                            candidateFullName,
                            streamResponseFullName,
                            GetFullyQualifiedName(streamHandlerSymbol),
                            wrapperFieldName,
                            candidate.DeclaredAccessibility));
                    }
                }
            }
        }

        // Collect pipeline behavior registrations — multiple behaviors per type are allowed (pipeline chain).
        // Scan only types declared in the *current* compilation (not referenced assemblies) so each
        // consumer's generator registers its own behaviors. Core's opt-in addons (Mutex, Retry, ...)
        // register their behaviors explicitly — this scan deliberately won't duplicate them for
        // consumers that reference Core.
        var iPipelineBehaviorSymbol = compilation.GetTypeByMetadataName(IPipelineBehaviorMetadataName);
        var iStreamPipelineBehaviorSymbol = compilation.GetTypeByMetadataName(IStreamPipelineBehaviorMetadataName);

        var publishBehaviorRegistrations = new List<(string BehaviorFullName, string InterfaceFullName)>();
        var pipelineBehaviorRegistrations = new List<(string BehaviorFullName, string InterfaceFullName)>();
        var streamPipelineBehaviorRegistrations = new List<(string BehaviorFullName, string InterfaceFullName)>();

        foreach (var candidateType in candidates)
        {
            if (candidateType is null || candidateType.IsAbstract || candidateType.TypeKind == TypeKind.Interface
                || candidateType.ContainingType is not null || candidateType.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            var isOpenGeneric = candidateType.IsGenericType;

            foreach (var iface in candidateType.AllInterfaces)
            {
                if (iPublishBehaviorSymbol is not null
                    && iface.OriginalDefinition.Equals(iPublishBehaviorSymbol, SymbolEqualityComparer.Default))
                {
                    AddBehaviorRegistration(
                        publishBehaviorRegistrations,
                        candidateType,
                        iface,
                        isOpenGeneric,
                        "global::Warp.Core.Handlers.IPublishPipelineBehavior");
                    continue;
                }

                if (iPipelineBehaviorSymbol is not null
                    && iface.OriginalDefinition.Equals(iPipelineBehaviorSymbol, SymbolEqualityComparer.Default))
                {
                    AddBehaviorRegistration(
                        pipelineBehaviorRegistrations,
                        candidateType,
                        iface,
                        isOpenGeneric,
                        "global::Warp.Core.Handlers.IPipelineBehavior");
                    continue;
                }

                if (iStreamPipelineBehaviorSymbol is not null
                    && iface.OriginalDefinition.Equals(iStreamPipelineBehaviorSymbol, SymbolEqualityComparer.Default))
                {
                    AddBehaviorRegistration(
                        streamPipelineBehaviorRegistrations,
                        candidateType,
                        iface,
                        isOpenGeneric,
                        "global::Warp.Core.Handlers.IStreamPipelineBehavior");
                    continue;
                }
            }
        }

        if (requestTypes.Count == 0 && streamRequestTypes.Count == 0 && jobTypes.Count == 0
            && publishBehaviorRegistrations.Count == 0 && pipelineBehaviorRegistrations.Count == 0
            && streamPipelineBehaviorRegistrations.Count == 0)
        {
            return;
        }

        var source = GenerateSource(
            requestTypes,
            streamRequestTypes,
            jobTypes,
            publishBehaviorRegistrations,
            pipelineBehaviorRegistrations,
            streamPipelineBehaviorRegistrations);
        context.AddSource("WarpMediator.g.cs", source);
    }

    /// <summary>
    /// Emits one behavior registration. Open-generic implementations (e.g. <c>Foo&lt;T&gt;</c>
    /// implementing <c>IPublishPipelineBehavior&lt;T&gt;</c>) are registered as open generics so
    /// the DI container closes them at resolve time — the same behavior the reflection-based
    /// <c>AddPipelineBehaviors(assembly)</c> provided.
    /// </summary>
    private static void AddBehaviorRegistration(
        List<(string BehaviorFullName, string InterfaceFullName)> registrations,
        INamedTypeSymbol behaviorType,
        INamedTypeSymbol iface,
        bool isOpenGeneric,
        string interfaceNamePrefix)
    {
        if (isOpenGeneric)
        {
            var behaviorCommas = new string(',', behaviorType.TypeParameters.Length - 1);
            var ifaceCommas = new string(',', iface.TypeArguments.Length - 1);
            var behaviorTypeOf = $"typeof({GetOpenGenericContainingName(behaviorType)}<{behaviorCommas}>)";
            var ifaceTypeOf = $"typeof({interfaceNamePrefix}<{ifaceCommas}>)";
            registrations.Add((behaviorTypeOf, ifaceTypeOf));
            return;
        }

        var behaviorFullName = GetFullyQualifiedName(behaviorType);
        var typeArgs = string.Join(", ", iface.TypeArguments.Select(GetFullyQualifiedName));
        var interfaceFullName = $"{interfaceNamePrefix}<{typeArgs}>";
        registrations.Add((behaviorFullName, interfaceFullName));
    }

    /// <summary>
    /// Returns the fully-qualified name of <paramref name="type"/> without the generic
    /// arity suffix — e.g. <c>global::Foo.Bar</c> for <c>Bar&lt;T&gt;</c>.
    /// </summary>
    private static string GetOpenGenericContainingName(INamedTypeSymbol type)
    {
        var display = type.ToDisplayString(
            new SymbolDisplayFormat(
                globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
                typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
                genericsOptions: SymbolDisplayGenericsOptions.None));

        return display;
    }

    private static Dictionary<string, INamedTypeSymbol> BuildHandlerMap(
        Compilation compilation,
        INamedTypeSymbol iRequestHandlerSymbol)
    {
        var map = new Dictionary<string, INamedTypeSymbol>();

        foreach (var type in GetAllTypes(compilation))
        {
            if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.OriginalDefinition.Equals(iRequestHandlerSymbol, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var requestType = iface.TypeArguments[0];
                var responseType = iface.TypeArguments[1];
                var key = GetFullyQualifiedName(requestType) + "|" + GetFullyQualifiedName(responseType);
                map[key] = type;
            }
        }

        return map;
    }

    /// <summary>
    /// Build handler map for single-type-param interfaces where only one handler per type is
    /// allowed (IJobHandler&lt;T&gt;). If multiple implementers exist the last one wins — but
    /// that's a user error, not our problem.
    /// </summary>
    private static Dictionary<string, INamedTypeSymbol> BuildSingleTypeHandlerMap(
        Compilation compilation,
        INamedTypeSymbol handlerInterfaceSymbol)
    {
        var map = new Dictionary<string, INamedTypeSymbol>();

        foreach (var type in GetAllTypes(compilation))
        {
            if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.OriginalDefinition.Equals(handlerInterfaceSymbol, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var messageType = iface.TypeArguments[0];
                var key = GetFullyQualifiedName(messageType);
                map[key] = type;
            }
        }

        return map;
    }

    /// <summary>
    /// Build handler map for single-type-param interfaces that allow multiple handlers per type
    /// (IMessageHandler&lt;T&gt; — pub/sub semantics). Preserves declaration order across the
    /// compilation + reference assemblies.
    /// </summary>
    private static Dictionary<string, List<INamedTypeSymbol>> BuildSingleTypeMultiHandlerMap(
        Compilation compilation,
        INamedTypeSymbol handlerInterfaceSymbol)
    {
        var map = new Dictionary<string, List<INamedTypeSymbol>>();

        foreach (var type in GetAllTypes(compilation))
        {
            if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var iface in type.AllInterfaces)
            {
                if (!iface.OriginalDefinition.Equals(handlerInterfaceSymbol, SymbolEqualityComparer.Default))
                {
                    continue;
                }

                var messageType = iface.TypeArguments[0];
                var key = GetFullyQualifiedName(messageType);
                if (!map.TryGetValue(key, out var list))
                {
                    list = [];
                    map[key] = list;
                }

                list.Add(type);
            }
        }

        return map;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.GlobalNamespace);

        foreach (var reference in compilation.References)
        {
            if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
            {
                stack.Push(assembly.GlobalNamespace);
            }
        }

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is INamedTypeSymbol namedType)
            {
                yield return namedType;
                foreach (var nested in namedType.GetTypeMembers())
                {
                    stack.Push(nested);
                }
            }

            if (current is INamespaceSymbol ns)
            {
                foreach (var member in ns.GetMembers())
                {
                    if (member is INamespaceOrTypeSymbol nsOrType)
                    {
                        stack.Push(nsOrType);
                    }
                }
            }
        }
    }

    private static string GetFullyQualifiedName(ISymbol symbol)
    {
        return symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static string GenerateSource(
        List<RequestTypeInfo> requestTypes,
        List<StreamRequestTypeInfo> streamRequestTypes,
        List<JobTypeInfo> jobTypes,
        List<(string BehaviorFullName, string InterfaceFullName)> publishBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> pipelineBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> streamPipelineBehaviorRegistrations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Warp.Core.Handlers;");
        sb.AppendLine();
        sb.AppendLine("namespace Warp.Core.Handlers.Generated");
        sb.AppendLine("{");

        if (requestTypes.Count > 0 || streamRequestTypes.Count > 0)
        {
            GenerateMediatorCode(sb, requestTypes, streamRequestTypes);
        }

        if (jobTypes.Count > 0)
        {
            GenerateJobDispatcherCode(sb, jobTypes);
        }

        GenerateDIRegistration(sb, requestTypes, streamRequestTypes, jobTypes, publishBehaviorRegistrations, pipelineBehaviorRegistrations, streamPipelineBehaviorRegistrations);

        GenerateModuleInitializer(sb);

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateMediatorCode(StringBuilder sb, List<RequestTypeInfo> requestTypes, List<StreamRequestTypeInfo> streamRequestTypes)
    {
        if (requestTypes.Count > 0)
        {
            // RequestHandlerWrapper<TRequest, TResponse>
            sb.AppendLine("    internal sealed class RequestHandlerWrapper<TRequest, TResponse>");
            sb.AppendLine("        where TRequest : global::Warp.Core.Handlers.IRequest<TResponse>");
            sb.AppendLine("    {");
            sb.AppendLine("        private global::Warp.Core.Handlers.RequestHandlerDelegate<TRequest, TResponse> _rootHandler = null!;");
            sb.AppendLine();
            sb.AppendLine("        public void Init(global::System.IServiceProvider sp)");
            sb.AppendLine("        {");
            sb.AppendLine("            var handler = sp.GetRequiredService<global::Warp.Core.Handlers.IRequestHandler<TRequest, TResponse>>();");
            sb.AppendLine("            var behaviors = sp.GetServices<global::Warp.Core.Handlers.IPipelineBehavior<TRequest, TResponse>>().ToArray();");
            sb.AppendLine("            global::Warp.Core.Handlers.RequestHandlerDelegate<TRequest, TResponse> chain = handler.HandleAsync;");
            sb.AppendLine("            for (int i = behaviors.Length - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                var b = behaviors[i];");
            sb.AppendLine("                var next = chain;");
            sb.AppendLine("                chain = (req, ct) => b.HandleAsync(req, next, ct);");
            sb.AppendLine("            }");
            sb.AppendLine("            _rootHandler = chain;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public global::System.Threading.Tasks.Task<TResponse> Handle(TRequest request, global::System.Threading.CancellationToken ct)");
            sb.AppendLine("            => _rootHandler(request, ct);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        if (streamRequestTypes.Count > 0)
        {
            // StreamHandlerWrapper<TRequest, TResponse>
            sb.AppendLine("    internal sealed class StreamHandlerWrapper<TRequest, TResponse>");
            sb.AppendLine("        where TRequest : global::Warp.Core.Handlers.IStreamRequest<TResponse>");
            sb.AppendLine("    {");
            sb.AppendLine("        private global::Warp.Core.Handlers.StreamHandlerDelegate<TRequest, TResponse> _streamHandler = null!;");
            sb.AppendLine("        private global::Warp.Core.Handlers.RequestHandlerDelegate<TRequest, global::System.Collections.Generic.IAsyncEnumerable<TResponse>> _requestChain = null!;");
            sb.AppendLine("        private bool _hasRequestBehaviors;");
            sb.AppendLine();
            sb.AppendLine("        public void Init(global::System.IServiceProvider sp)");
            sb.AppendLine("        {");
            sb.AppendLine("            var handler = sp.GetRequiredService<global::Warp.Core.Handlers.IStreamRequestHandler<TRequest, TResponse>>();");
            sb.AppendLine("            var streamBehaviors = sp.GetServices<global::Warp.Core.Handlers.IStreamPipelineBehavior<TRequest, TResponse>>().ToArray();");
            sb.AppendLine("            global::Warp.Core.Handlers.StreamHandlerDelegate<TRequest, TResponse> streamChain = handler.HandleAsync;");
            sb.AppendLine("            for (int i = streamBehaviors.Length - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                var b = streamBehaviors[i];");
            sb.AppendLine("                var next = streamChain;");
            sb.AppendLine("                streamChain = (req, ct) => b.HandleAsync(req, next, ct);");
            sb.AppendLine("            }");
            sb.AppendLine("            _streamHandler = streamChain;");
            sb.AppendLine();
            sb.AppendLine("            var requestBehaviors = sp.GetServices<global::Warp.Core.Handlers.IPipelineBehavior<TRequest, global::System.Collections.Generic.IAsyncEnumerable<TResponse>>>().ToArray();");
            sb.AppendLine("            _hasRequestBehaviors = requestBehaviors.Length > 0;");
            sb.AppendLine("            global::Warp.Core.Handlers.RequestHandlerDelegate<TRequest, global::System.Collections.Generic.IAsyncEnumerable<TResponse>> requestChain =");
            sb.AppendLine("                (req, ct) => global::System.Threading.Tasks.Task.FromResult(streamChain(req, ct));");
            sb.AppendLine("            for (int i = requestBehaviors.Length - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                var b = requestBehaviors[i];");
            sb.AppendLine("                var next = requestChain;");
            sb.AppendLine("                requestChain = (req, ct) => b.HandleAsync(req, next, ct);");
            sb.AppendLine("            }");
            sb.AppendLine("            _requestChain = requestChain;");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        public global::System.Collections.Generic.IAsyncEnumerable<TResponse> Handle(TRequest request, global::System.Threading.CancellationToken ct)");
            sb.AppendLine("        {");
            sb.AppendLine("            if (!_hasRequestBehaviors)");
            sb.AppendLine("            {");
            sb.AppendLine("                return _streamHandler(request, ct);");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            return UnwrapStreamTask(_requestChain(request, ct), ct);");
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine("        private static async global::System.Collections.Generic.IAsyncEnumerable<TResponse> UnwrapStreamTask(");
            sb.AppendLine("            global::System.Threading.Tasks.Task<global::System.Collections.Generic.IAsyncEnumerable<TResponse>> task,");
            sb.AppendLine("            [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken ct)");
            sb.AppendLine("        {");
            sb.AppendLine("            var enumerable = await task;");
            sb.AppendLine("            await foreach (var item in enumerable.WithCancellation(ct))");
            sb.AppendLine("            {");
            sb.AppendLine("                yield return item;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // GeneratedMediator
        sb.AppendLine("    public sealed class GeneratedMediator : global::Warp.Core.Handlers.IMediator");
        sb.AppendLine("    {");
        foreach (var req in requestTypes)
        {
            sb.AppendLine($"        private readonly RequestHandlerWrapper<{req.RequestFullName}, {req.ResponseFullName}> {req.WrapperFieldName};");
        }

        foreach (var stream in streamRequestTypes)
        {
            sb.AppendLine($"        private readonly StreamHandlerWrapper<{stream.RequestFullName}, {stream.ResponseFullName}> {stream.WrapperFieldName};");
        }

        sb.AppendLine();
        sb.AppendLine("        public GeneratedMediator(global::System.IServiceProvider sp)");
        sb.AppendLine("        {");
        foreach (var req in requestTypes)
        {
            sb.AppendLine($"            {req.WrapperFieldName} = new RequestHandlerWrapper<{req.RequestFullName}, {req.ResponseFullName}>();");
            sb.AppendLine($"            {req.WrapperFieldName}.Init(sp);");
        }

        foreach (var stream in streamRequestTypes)
        {
            sb.AppendLine($"            {stream.WrapperFieldName} = new StreamHandlerWrapper<{stream.RequestFullName}, {stream.ResponseFullName}>();");
            sb.AppendLine($"            {stream.WrapperFieldName}.Init(sp);");
        }

        sb.AppendLine("        }");
        sb.AppendLine();

        // Send overloads
        foreach (var req in requestTypes)
        {
            sb.AppendLine($"        public global::System.Threading.Tasks.Task<{req.ResponseFullName}> Send({req.RequestFullName} request, global::System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine($"            => {req.WrapperFieldName}.Handle(request, cancellationToken);");
            sb.AppendLine();
        }

        sb.AppendLine("        public global::System.Threading.Tasks.Task<TResponse> Send<TResponse>(global::Warp.Core.Handlers.IRequest<TResponse> request, global::System.Threading.CancellationToken cancellationToken = default)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (request)");
        sb.AppendLine("            {");
        foreach (var req in requestTypes)
        {
            sb.AppendLine($"                case {req.RequestFullName} r:");
            sb.AppendLine($"                    return (global::System.Threading.Tasks.Task<TResponse>)(object)Send(r, cancellationToken);");
        }

        sb.AppendLine("                default:");
        sb.AppendLine("                    throw new global::System.InvalidOperationException($\"No handler registered for {request.GetType().Name}\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine();

        // CreateStream overloads
        foreach (var stream in streamRequestTypes)
        {
            sb.AppendLine($"        public global::System.Collections.Generic.IAsyncEnumerable<{stream.ResponseFullName}> CreateStream({stream.RequestFullName} request, global::System.Threading.CancellationToken cancellationToken = default)");
            sb.AppendLine($"            => {stream.WrapperFieldName}.Handle(request, cancellationToken);");
            sb.AppendLine();
        }

        sb.AppendLine("        public global::System.Collections.Generic.IAsyncEnumerable<TResponse> CreateStream<TResponse>(global::Warp.Core.Handlers.IStreamRequest<TResponse> request, global::System.Threading.CancellationToken cancellationToken = default)");
        sb.AppendLine("        {");
        sb.AppendLine("            switch (request)");
        sb.AppendLine("            {");
        foreach (var stream in streamRequestTypes)
        {
            sb.AppendLine($"                case {stream.RequestFullName} r:");
            sb.AppendLine($"                    return (global::System.Collections.Generic.IAsyncEnumerable<TResponse>)(object)CreateStream(r, cancellationToken);");
        }

        sb.AppendLine("                default:");
        sb.AppendLine("                    throw new global::System.InvalidOperationException($\"No stream handler registered for {request.GetType().Name}\");");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateJobDispatcherCode(StringBuilder sb, List<JobTypeInfo> jobTypes)
    {
        sb.AppendLine("    public static class GeneratedJobDispatcher");
        sb.AppendLine("    {");

        // TryExecute — switches on messageType, returns null if not a known type
        sb.AppendLine("        public static global::System.Threading.Tasks.Task? TryExecute(");
        sb.AppendLine("            object message,");
        sb.AppendLine("            global::System.Type messageType,");
        sb.AppendLine("            global::System.Type handlerType,");
        sb.AppendLine("            global::System.IServiceProvider provider,");
        sb.AppendLine("            global::System.Threading.CancellationToken cancellationToken)");
        sb.AppendLine("        {");
        foreach (var job in jobTypes)
        {
            sb.AppendLine($"            if (messageType == typeof({job.JobFullName}))");
            sb.AppendLine($"                return {job.MethodName}(({job.JobFullName})message, handlerType, provider, cancellationToken);");
        }

        sb.AppendLine("            return null;");
        sb.AppendLine("        }");
        sb.AppendLine();

        // Per-type execute methods
        foreach (var job in jobTypes)
        {
            sb.AppendLine($"        private static async global::System.Threading.Tasks.Task {job.MethodName}(");
            sb.AppendLine($"            {job.JobFullName} message,");
            sb.AppendLine($"            global::System.Type handlerType,");
            sb.AppendLine($"            global::System.IServiceProvider provider,");
            sb.AppendLine($"            global::System.Threading.CancellationToken cancellationToken)");
            sb.AppendLine("        {");

            if (job.IsMessage)
            {
                // IMessage: multiple handlers possible, resolve by handlerType
                sb.AppendLine($"            var allHandlers = provider.GetServices<global::Warp.Core.Handlers.IMessageHandler<{job.JobFullName}>>();");
                sb.AppendLine("            var handler = allHandlers.First(h => h!.GetType() == handlerType);");
            }
            else
            {
                // IJob: single handler
                sb.AppendLine($"            var handler = provider.GetRequiredService<global::Warp.Core.Handlers.IJobHandler<{job.JobFullName}>>();");
            }

            sb.AppendLine($"            var behaviors = provider.GetServices<global::Warp.Core.Handlers.IPipelineBehavior<{job.JobFullName}, global::Warp.Core.Handlers.Unit>>().ToArray();");
            sb.AppendLine();
            sb.AppendLine($"            global::Warp.Core.Handlers.RequestHandlerDelegate<{job.JobFullName}, global::Warp.Core.Handlers.Unit> chain = async (req, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine("                await handler.HandleAsync(req, ct);");
            sb.AppendLine("                return global::Warp.Core.Handlers.Unit.Value;");
            sb.AppendLine("            };");
            sb.AppendLine();
            sb.AppendLine("            for (int i = behaviors.Length - 1; i >= 0; i--)");
            sb.AppendLine("            {");
            sb.AppendLine("                var b = behaviors[i];");
            sb.AppendLine("                var next = chain;");
            sb.AppendLine("                chain = (req, ct) => b.HandleAsync(req, next, ct);");
            sb.AppendLine("            }");
            sb.AppendLine();
            sb.AppendLine("            await chain(message, cancellationToken);");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static void GenerateDIRegistration(
        StringBuilder sb,
        List<RequestTypeInfo> requestTypes,
        List<StreamRequestTypeInfo> streamRequestTypes,
        List<JobTypeInfo> jobTypes,
        List<(string BehaviorFullName, string InterfaceFullName)> publishBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> pipelineBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> streamPipelineBehaviorRegistrations)
    {
        // Internal (not public) so consumer projects that each emit their own copy don't
        // collide with CS0436 across project references. Module-initializer wiring below
        // calls the extension intra-assembly; cross-assembly registration happens via the
        // emitted ModuleInitializer in each consuming assembly, not via direct calls.
        sb.AppendLine("    internal static class WarpMediatorServiceExtensions");
        sb.AppendLine("    {");
        sb.AppendLine("        public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddWarpMediator(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("        {");

        EmitRegistrations(sb, "            ", requestTypes, streamRequestTypes, jobTypes, publishBehaviorRegistrations, pipelineBehaviorRegistrations, streamPipelineBehaviorRegistrations);

        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static void EmitRegistrations(
        StringBuilder sb,
        string indent,
        List<RequestTypeInfo> requestTypes,
        List<StreamRequestTypeInfo> streamRequestTypes,
        List<JobTypeInfo> jobTypes,
        List<(string BehaviorFullName, string InterfaceFullName)> publishBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> pipelineBehaviorRegistrations,
        List<(string BehaviorFullName, string InterfaceFullName)> streamPipelineBehaviorRegistrations)
    {
        foreach (var req in requestTypes)
        {
            sb.AppendLine($"{indent}services.AddTransient<global::Warp.Core.Handlers.IRequestHandler<{req.RequestFullName}, {req.ResponseFullName}>, {req.HandlerFullName}>();");
        }

        foreach (var stream in streamRequestTypes)
        {
            sb.AppendLine($"{indent}services.AddTransient<global::Warp.Core.Handlers.IStreamRequestHandler<{stream.RequestFullName}, {stream.ResponseFullName}>, {stream.HandlerFullName}>();");
        }

        foreach (var job in jobTypes)
        {
            if (job.IsMessage)
            {
                foreach (var handlerFullName in job.HandlerFullNames)
                {
                    sb.AppendLine($"{indent}services.AddTransient<global::Warp.Core.Handlers.IMessageHandler<{job.JobFullName}>, {handlerFullName}>();");
                }
            }
            else
            {
                sb.AppendLine($"{indent}services.AddTransient<global::Warp.Core.Handlers.IJobHandler<{job.JobFullName}>, {job.HandlerFullNames[0]}>();");
            }
        }

        EmitBehaviorRegistrations(sb, indent, publishBehaviorRegistrations);
        EmitBehaviorRegistrations(sb, indent, pipelineBehaviorRegistrations);
        EmitBehaviorRegistrations(sb, indent, streamPipelineBehaviorRegistrations);

        if (requestTypes.Count > 0 || streamRequestTypes.Count > 0)
        {
            sb.AppendLine($"{indent}services.AddScoped<global::Warp.Core.Handlers.IMediator, GeneratedMediator>();");
        }
    }

    private static void EmitBehaviorRegistrations(
        StringBuilder sb,
        string indent,
        List<(string BehaviorFullName, string InterfaceFullName)> registrations)
    {
        foreach (var (behaviorFullName, interfaceFullName) in registrations)
        {
            if (behaviorFullName.StartsWith("typeof(", StringComparison.Ordinal))
            {
                sb.AppendLine($"{indent}services.AddTransient({interfaceFullName}, {behaviorFullName});");
            }
            else
            {
                sb.AppendLine($"{indent}services.AddTransient<{interfaceFullName}, {behaviorFullName}>();");
            }
        }
    }

    private static void GenerateModuleInitializer(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("    internal static class WarpGeneratedRegistrationModuleInit");
        sb.AppendLine("    {");
        sb.AppendLine("        [global::System.Runtime.CompilerServices.ModuleInitializer]");
        sb.AppendLine("        internal static void Register()");
        sb.AppendLine("        {");
        sb.AppendLine("            global::Warp.Core.Handlers.WarpGeneratedHandlerRegistry.Add(static services =>");
        sb.AppendLine("            {");
        sb.AppendLine("                WarpMediatorServiceExtensions.AddWarpMediator(services);");
        sb.AppendLine("            });");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
}

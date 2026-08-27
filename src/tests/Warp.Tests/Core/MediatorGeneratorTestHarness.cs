using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Warp.Core.Handlers;
using Warp.SourceGenerator;

namespace Warp.Tests.Core;

/// <summary>
/// Drives <see cref="WarpMediatorGenerator"/> against ad-hoc source, optionally with a separately
/// compiled "contracts" assembly referenced by the primary compilation. Lets the cross-assembly
/// handler-discovery tests exercise shared-contract layouts without standing up real projects.
/// </summary>
internal static class MediatorGeneratorTestHarness
{
    /// <summary>
    /// Runs the generator over <paramref name="primarySource"/>. When <paramref name="referencedSource"/>
    /// is supplied it is compiled into its own assembly first and added as a metadata reference, so
    /// types it declares are visible to the primary compilation exactly as a referenced package would be.
    /// </summary>
    public static string RunAndConcatGeneratedSources(
        string primarySource,
        string? referencedSource = null,
        string primaryAssemblyName = "Worker",
        string referencedAssemblyName = "Contracts")
    {
        var compilation = CreateCompilation(primarySource, referencedSource, primaryAssemblyName, referencedAssemblyName);

        var driver = CSharpGeneratorDriver.Create(new WarpMediatorGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();

        return string.Join("\n", result.GeneratedTrees.Select(x => x.ToString()));
    }

    /// <summary>
    /// Returns the diagnostics the GENERATOR reported (WARP001-003). These live on the run result and
    /// never appear in <c>Compilation.GetDiagnostics</c>, so this and
    /// <see cref="RunAndGetCompilationErrors"/> see disjoint sets.
    /// </summary>
    public static IReadOnlyList<Diagnostic> RunAndGetGeneratorDiagnostics(
        string primarySource,
        string? referencedSource = null,
        string primaryAssemblyName = "Worker",
        string referencedAssemblyName = "Contracts")
    {
        var compilation = CreateCompilation(primarySource, referencedSource, primaryAssemblyName, referencedAssemblyName);

        var driver = CSharpGeneratorDriver.Create(new WarpMediatorGenerator());

        return [.. driver.RunGenerators(compilation).GetRunResult().Diagnostics];
    }

    /// <summary>
    /// Runs the generator and compiles the resulting code together with <paramref name="primarySource"/>,
    /// returning the error-severity diagnostics. Lets tests assert the generated mediator is valid C#
    /// (e.g. no duplicate-member collisions, CS0102 / CS0111).
    /// </summary>
    public static IReadOnlyList<Diagnostic> RunAndGetCompilationErrors(
        string primarySource,
        string? referencedSource = null,
        string primaryAssemblyName = "Worker",
        string referencedAssemblyName = "Contracts")
    {
        var compilation = CreateCompilation(primarySource, referencedSource, primaryAssemblyName, referencedAssemblyName);

        var driver = CSharpGeneratorDriver.Create(new WarpMediatorGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);

        return [.. updated.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error)];
    }

    private static CSharpCompilation CreateCompilation(
        string primarySource,
        string? referencedSource,
        string primaryAssemblyName,
        string referencedAssemblyName)
    {
        var baseReferences = BuildBaseReferences();

        var references = baseReferences;
        if (referencedSource is not null)
        {
            references = references.Add(CompileToReference(referencedSource, referencedAssemblyName, baseReferences));
        }

        return CSharpCompilation.Create(
            assemblyName: primaryAssemblyName,
            syntaxTrees: [CSharpSyntaxTree.ParseText(primarySource)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static PortableExecutableReference CompileToReference(
        string source,
        string assemblyName,
        ImmutableArray<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [CSharpSyntaxTree.ParseText(source)],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var emitResult = compilation.Emit(stream);
        if (!emitResult.Success)
        {
            var diagnostics = string.Join("\n", emitResult.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error));
            throw new InvalidOperationException($"Failed to compile referenced assembly '{assemblyName}':\n{diagnostics}");
        }

        stream.Position = 0;
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static ImmutableArray<MetadataReference> BuildBaseReferences()
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(x => !x.IsDynamic && !string.IsNullOrEmpty(x.Location))
            .Select(x => MetadataReference.CreateFromFile(x.Location))
            .Cast<MetadataReference>()
            .ToImmutableArray();

        // Be explicit about the Warp.Core assembly, which defines IRequest, IJob, IMessage and the
        // handler interfaces. The AppDomain scan normally already includes it via the project reference.
        var coreAssembly = typeof(IRequest<>).Assembly;
        if (!references.Any(x => string.Equals(x.Display, coreAssembly.Location, StringComparison.OrdinalIgnoreCase)))
        {
            references = references.Add(MetadataReference.CreateFromFile(coreAssembly.Location));
        }

        return references;
    }
}

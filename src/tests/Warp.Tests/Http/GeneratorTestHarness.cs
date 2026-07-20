using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Warp.Core.Handlers;
using Warp.Http;
using Warp.Http.SourceGenerator;

namespace Warp.Tests.Http;

/// <summary>
/// Drives <see cref="WarpHttpGenerator"/> against ad-hoc source. Used by
/// <see cref="DiagnosticsTests"/> to exercise WHTTP rules without needing the
/// offending types to live in the main test compilation.
/// </summary>
internal static class GeneratorTestHarness
{
    public static GeneratorDriverRunResult Run(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Build a DETERMINISTIC reference set. An AppDomain.GetAssemblies() scan is load-order
        // dependent — assemblies load lazily, so which ones are present when a given test runs
        // varies (adding the adapter project references shifted the set so that
        // Microsoft.AspNetCore.Mvc.Core, which defines [FromBody]/[FromRoute]/[FromForm], was no
        // longer loaded, and the generator's GetTypeByMetadataName lookups for those attributes
        // returned null — silently degrading body/form binding classification and suppressing
        // WHTTP004/WHTTP006). TRUSTED_PLATFORM_ASSEMBLIES is the fixed list of platform + shared
        // framework assemblies (including the full Microsoft.AspNetCore.App set), independent of
        // load order. Anchor the Warp assemblies the test sources use via typeof, and dedup by
        // file path so a TPA entry and a typeof anchor for the same assembly don't both appear.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencesBuilder = ImmutableArray.CreateBuilder<MetadataReference>();

        void AddReference(string? path)
        {
            if (!string.IsNullOrEmpty(path) && seenPaths.Add(path))
            {
                referencesBuilder.Add(MetadataReference.CreateFromFile(path));
            }
        }

        var trustedPlatformAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator))
        {
            AddReference(path);
        }

        // Anchor the Warp assemblies the test sources reference (IRequest/IJob/IMessage and the
        // Warp.Http attributes). They are typically already in TPA as app dependencies; the dedup
        // above keeps this idempotent.
        AddReference(typeof(IRequest<>).Assembly.Location);
        AddReference(typeof(WarpHttpAttribute).Assembly.Location);

        var references = referencesBuilder.ToImmutable();

        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorHarness",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new WarpHttpGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);
        return driver.RunGenerators(compilation).GetRunResult();
    }
}

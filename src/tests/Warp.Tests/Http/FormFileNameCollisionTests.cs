using Microsoft.CodeAnalysis;
using Shouldly;

namespace Warp.Tests.Http;

/// <summary>
/// A bare <c>IFormFile</c> is bound by PARAMETER NAME (Swashbuckle rejects the <c>[FromForm(Name)]</c>
/// shape), so the request's property name becomes a lambda parameter in the generated Mixed handler.
/// The lambda's own locals must therefore never be reachable from a user-chosen property name.
/// </summary>
[Trait("Category", "NoDb")]
public class FormFileNameCollisionTests
{
    [TimedTheory]
    [InlineData("ctx")]
    [InlineData("request")]
    [InlineData("body")]
    public void FormFileNamedLikeALambdaLocal_GeneratedHandlerCompiles(string propertyName)
    {
        var source = $$"""
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;
            using Warp.Http;

            namespace TestSamples;

            public sealed record Upload([FromRoute] int Id, IFormFile {{propertyName}}) : IRequest<string>;

            [WarpHttpPost("/api/folders/{id}/docs")]
            public sealed class UploadHandler : IRequestHandler<Upload, string>
            {
                public Task<string> HandleAsync(Upload request, CancellationToken ct) => Task.FromResult(request.{{propertyName}}.FileName);
            }
            """;

        var (result, output) = GeneratorTestHarness.RunWithOutput(source);

        result.Diagnostics.ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);

        var errors = output.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();
        errors.ShouldBeEmpty();

        // Still bound by name (the Swashbuckle-safe shape), not via the [FromForm(Name)] fallback.
        var generated = string.Concat(result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString()));
        generated.ShouldContain($"IFormFile @{propertyName}");
        generated.ShouldNotContain("FromForm(Name");
    }

    [TimedFact]
    public void FormFileWithReservedPrefixName_FallsBackToAttributeBinding()
    {
        // `__`-prefixed names are the lambda's own; a property using the prefix cannot be bound by name.
        const string source = """
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;
            using Warp.Core.Handlers;
            using Warp.Http;

            namespace TestSamples;

            public sealed record Upload([FromRoute] int Id, IFormFile __ctx) : IRequest<string>;

            [WarpHttpPost("/api/folders/{id}/docs")]
            public sealed class UploadHandler : IRequestHandler<Upload, string>
            {
                public Task<string> HandleAsync(Upload request, CancellationToken ct) => Task.FromResult(request.__ctx.FileName);
            }
            """;

        var (result, output) = GeneratorTestHarness.RunWithOutput(source);

        output.GetDiagnostics().ShouldNotContain(d => d.Severity == DiagnosticSeverity.Error);

        var generated = string.Concat(result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString()));
        generated.ShouldContain("FromForm(Name = \"__ctx\")");
    }
}

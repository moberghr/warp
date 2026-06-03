using Microsoft.CodeAnalysis;
using Shouldly;
using Warp.Tests.TestData;

namespace Warp.Tests.Http;

[Trait("Category", "NoDb")]
public sealed class DiagnosticsTests
{
    [TimedFact]
    public void WHTTP001_FiresWhenHandlerIsForIJobRequest()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;

            namespace TestSamples;

            public sealed record WorkJob : IJob;

            [WarpHttpPost("/jobs/work")]
            public sealed class WorkJobHandler : IJobHandler<WorkJob>
            {
                public Task HandleAsync(WorkJob request, System.Threading.CancellationToken ct) => Task.CompletedTask;
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        // Either form is acceptable: either the handler is rejected because IJobHandler isn't a
        // request handler (WHTTP001 path 1), or — if the user added IRequestHandler too — the
        // request type's IJob marker triggers WHTTP001 (path 2).
        diagnostics.ShouldContain(d => d.Id == "WHTTP001" && d.GetMessage().Contains("WorkJobHandler"));
    }

    [TimedFact]
    public void WHTTP001_FiresWhenHandlerImplementsBothIRequestHandlerAndIJobRequest()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record DualImplJob : IJob, IRequest<Unit>;

            [WarpHttpPost("/dual")]
            public sealed class DualHandler : IRequestHandler<DualImplJob, Unit>
            {
                public Task<Unit> HandleAsync(DualImplJob request, CancellationToken ct) => Task.FromResult(Unit.Value);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d =>
            d.Id == "WHTTP001"
            && d.Severity == DiagnosticSeverity.Error
            && d.GetMessage().Contains("DualHandler")
            && d.GetMessage().Contains("IJob"));
    }

    [TimedFact]
    public void WHTTP001_FiresWhenAttributeIsOnNonHandlerType()
    {
        const string source = """
            using Warp.Http;

            namespace TestSamples;

            [WarpHttpGet("/random")]
            public sealed class RandomBag
            {
                public string? Tag { get; set; }
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d => d.Id == "WHTTP001" && d.GetMessage().Contains("RandomBag"));
    }

    [TimedFact]
    public void WHTTP001_FiresWhenHandlerIsForIMessageRequest()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record NotifyMessage : IMessage, IRequest<Unit>;

            [WarpHttpPost("/messages/notify")]
            public sealed class NotifyHandler : IRequestHandler<NotifyMessage, Unit>
            {
                public Task<Unit> HandleAsync(NotifyMessage request, CancellationToken ct) => Task.FromResult(Unit.Value);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d => d.Id == "WHTTP001" && d.GetMessage().Contains("IMessage"));
    }

    [TimedFact]
    public void WHTTP001_DoesNotFireForPureRequestHandler()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record CreateOrder(string Name) : IRequest<string>;

            [WarpHttpPost("/orders")]
            public sealed class CreateOrderHandler : IRequestHandler<CreateOrder, string>
            {
                public Task<string> HandleAsync(CreateOrder request, CancellationToken ct) => Task.FromResult(request.Name);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP001");
    }

    [TimedFact]
    public void WHTTP001_DoesNotFireForPureStreamRequestHandler()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Collections.Generic;
            using System.Runtime.CompilerServices;

            namespace TestSamples;

            public sealed record OrderFeed : IStreamRequest<int>;

            [WarpHttpGet("/feed")]
            public sealed class OrderFeedHandler : IStreamRequestHandler<OrderFeed, int>
            {
                public async IAsyncEnumerable<int> HandleAsync(OrderFeed request, [EnumeratorCancellation] CancellationToken ct)
                {
                    yield return 1;
                    await System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP001");
    }

    [TimedFact]
    public void WHTTP002_FiresWhenMultiAttributeMissingNameOnAny()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record MultiNoName(string Tag) : IRequest<string>;

            [WarpHttpPost("/v1/x", Name = "X1")]
            [WarpHttpPost("/v2/x")]
            public sealed class MultiNoNameHandler : IRequestHandler<MultiNoName, string>
            {
                public Task<string> HandleAsync(MultiNoName request, CancellationToken ct) => Task.FromResult(request.Tag);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d => d.Id == "WHTTP002" && d.GetMessage().Contains("MultiNoNameHandler"));
    }

    [TimedFact]
    public void WHTTP002_DoesNotFireWhenAllAttributesHaveName()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record MultiNamed(string Tag) : IRequest<string>;

            [WarpHttpPost("/v1/x", Name = "X1")]
            [WarpHttpPost("/v2/x", Name = "X2")]
            public sealed class MultiNamedHandler : IRequestHandler<MultiNamed, string>
            {
                public Task<string> HandleAsync(MultiNamed request, CancellationToken ct) => Task.FromResult(request.Tag);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP002");
    }

    [TimedFact]
    public void WHTTP004_FiresOnBodyVerbWithRouteAndMultipleUnattributedParams()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record CreateThing([FromRoute] int TenantId, string Name, decimal Price) : IRequest<string>;

            [WarpHttpPost("/api/tenants/{tenantId}/things")]
            public sealed class CreateThingHandler : IRequestHandler<CreateThing, string>
            {
                public Task<string> HandleAsync(CreateThing request, CancellationToken ct) => Task.FromResult(request.Name);
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        result.Diagnostics.ShouldContain(d => d.Id == "WHTTP004" && d.GetMessage().Contains("CreateThingHandler"));

        // The generator must not also throw CS8785 ("Generator failed to generate source").
        result.Diagnostics.ShouldNotContain(d => d.Id == "CS8785");
    }

    [TimedFact]
    public void WHTTP004_FiresOnTwoExplicitFromBodyParams()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record DoubleBody([FromRoute] int Id, [FromBody] BodyA A, [FromBody] BodyB B) : IRequest<string>;

            public sealed record BodyA(string X);
            public sealed record BodyB(string Y);

            [WarpHttpPost("/api/double-body/{id}")]
            public sealed class DoubleBodyHandler : IRequestHandler<DoubleBody, string>
            {
                public Task<string> HandleAsync(DoubleBody request, CancellationToken ct) => Task.FromResult(request.A.X);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d => d.Id == "WHTTP004" && d.GetMessage().Contains("DoubleBodyHandler"));
    }

    [TimedFact]
    public void WHTTP004_DoesNotFireOnSingleBodyTargetMixed()
    {
        // POST with [FromRoute] + one bare scalar parameter — Mixed shape with exactly one
        // body target. This is supported and must not trip WHTTP004.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record PromoteUser([FromRoute] int Id, string NewRole) : IRequest<string>;

            [WarpHttpPost("/api/users/{id}/promote")]
            public sealed class PromoteUserHandler : IRequestHandler<PromoteUser, string>
            {
                public Task<string> HandleAsync(PromoteUser request, CancellationToken ct) => Task.FromResult(request.NewRole);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP004");
    }

    [TimedFact]
    public void WHTTP004OnOneHandler_DoesNotWipeOtherHandlers()
    {
        // One bad handler (multi-body Mixed) sits alongside a good one. The bad handler is
        // diagnosed and skipped; the good handler must still register exactly one endpoint.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record BadRequest([FromRoute] int Id, string A, string B) : IRequest<string>;

            [WarpHttpPost("/api/bad/{id}")]
            public sealed class BadHandler : IRequestHandler<BadRequest, string>
            {
                public Task<string> HandleAsync(BadRequest request, CancellationToken ct) => Task.FromResult(request.A);
            }

            public sealed record GoodRequest(string Name) : IRequest<string>;

            [WarpHttpPost("/api/good")]
            public sealed class GoodHandler : IRequestHandler<GoodRequest, string>
            {
                public Task<string> HandleAsync(GoodRequest request, CancellationToken ct) => Task.FromResult(request.Name);
            }
            """;

        var result = GeneratorTestHarness.Run(source);

        result.Diagnostics.ShouldContain(d => d.Id == "WHTTP004" && d.GetMessage().Contains("BadHandler"));
        result.Diagnostics.ShouldNotContain(d => d.Id == "CS8785");

        var generated = string.Concat(result.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => s.SourceText.ToString()));

        // Exactly one endpoint registered, and it must be GoodHandler.
        CountOccurrences(generated, "WarpGeneratedHttpRegistry.Add").ShouldBe(1);
        generated.ShouldContain("typeof(global::TestSamples.GoodHandler)");
        generated.ShouldNotContain("typeof(global::TestSamples.BadHandler)");
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [TimedFact]
    public void WHTTP002_DoesNotFireForSingleAttribute()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record Single(string Tag) : IRequest<string>;

            [WarpHttpPost("/single")]
            public sealed class SingleHandler : IRequestHandler<Single, string>
            {
                public Task<string> HandleAsync(Single request, CancellationToken ct) => Task.FromResult(request.Tag);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP002");
    }

    [TimedFact]
    public void WHTTP005_FiresOnGetWithNonNullableScalarPropertyInitializer()
    {
        // The exact trap: GET request with a non-nullable scalar property carrying a C# default.
        // [AsParameters] ignores the initializer and makes Take a required query param → bare GET 400s.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed class ListTodos : IRequest<string>
            {
                public int Take { get; set; } = 20;
            }

            [WarpHttpGet("/api/todos")]
            public sealed class ListTodosHandler : IRequestHandler<ListTodos, string>
            {
                public Task<string> HandleAsync(ListTodos request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d =>
            d.Id == "WHTTP005"
            && d.Severity == DiagnosticSeverity.Warning
            && d.GetMessage().Contains("Take"));
    }

    [TimedFact]
    public void WHTTP005_FiresOnRecordPositionalParameterDefault()
    {
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record SearchTodos([FromQuery] int Page = 1) : IRequest<string>;

            [WarpHttpGet("/api/todos/search")]
            public sealed class SearchTodosHandler : IRequestHandler<SearchTodos, string>
            {
                public Task<string> HandleAsync(SearchTodos request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldContain(d => d.Id == "WHTTP005" && d.GetMessage().Contains("Page"));
    }

    [TimedFact]
    public void WHTTP005_DoesNotFireWhenScalarIsNullable()
    {
        // Nullable value type is the prescribed fix — ASP.NET treats it as optional. No warning.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed class ListTodos : IRequest<string>
            {
                public int? Take { get; set; } = 20;
            }

            [WarpHttpGet("/api/todos")]
            public sealed class ListTodosHandler : IRequestHandler<ListTodos, string>
            {
                public Task<string> HandleAsync(ListTodos request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP005");
    }

    [TimedFact]
    public void WHTTP005_DoesNotFireWhenNoDefaultValue()
    {
        // No C# default means the author intends a required filter — that's not the silent trap.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed class ListTodos : IRequest<string>
            {
                public int Take { get; set; }
            }

            [WarpHttpGet("/api/todos")]
            public sealed class ListTodosHandler : IRequestHandler<ListTodos, string>
            {
                public Task<string> HandleAsync(ListTodos request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP005");
    }

    [TimedFact]
    public void WHTTP005_DoesNotFireOnBodyVerb()
    {
        // POST binds TRequest from the JSON body, which honors C# defaults for omitted members.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed class CreateTodo : IRequest<string>
            {
                public int Priority { get; set; } = 3;
            }

            [WarpHttpPost("/api/todos")]
            public sealed class CreateTodoHandler : IRequestHandler<CreateTodo, string>
            {
                public Task<string> HandleAsync(CreateTodo request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP005");
    }

    [TimedFact]
    public void WHTTP005_DoesNotFireForStringQueryParameter()
    {
        // Reference-typed query params are already optional under ASP.NET binding — only
        // non-nullable value types become required. A defaulted string must not warn.
        const string source = """
            using Warp.Core.Handlers;
            using Warp.Http;
            using Microsoft.AspNetCore.Mvc;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestSamples;

            public sealed record SearchTodos([FromQuery] string Term = "") : IRequest<string>;

            [WarpHttpGet("/api/todos/search")]
            public sealed class SearchTodosHandler : IRequestHandler<SearchTodos, string>
            {
                public Task<string> HandleAsync(SearchTodos request, CancellationToken ct) => Task.FromResult(string.Empty);
            }
            """;

        var diagnostics = GeneratorTestHarness.Run(source).Diagnostics;

        diagnostics.ShouldNotContain(d => d.Id == "WHTTP005");
    }
}

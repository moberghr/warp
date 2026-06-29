using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Warp.Http.SourceGenerator;

internal enum BindingSource
{
    Body = 1,
    Route = 2,
    Query = 3,
    Header = 4,

    /// <summary>Multipart form field or uploaded file (IFormFile / IFormFileCollection / IFormCollection / [FromForm]).</summary>
    Form = 5,
}

internal enum BindingShape
{
    /// <summary>Single TRequest parameter — Minimal API binds the whole body for body verbs.</summary>
    WholeBody = 1,

    /// <summary>[AsParameters] TRequest — ASP.NET decomposes per property; no body part.</summary>
    AsParameters = 2,

    /// <summary>Per-source explicit parameters; we construct TRequest in the generated lambda.</summary>
    Mixed = 3,
}

internal sealed class BindingTarget
{
    public BindingTarget(string memberName, ITypeSymbol type, BindingSource source, string sourceKey, int? ctorParameterIndex, string? propertyName, bool hasClrDefault, Location? location, bool isWholeFormCollection = false)
    {
        MemberName = memberName;
        Type = type;
        Source = source;
        SourceKey = sourceKey;
        CtorParameterIndex = ctorParameterIndex;
        PropertyName = propertyName;
        HasClrDefault = hasClrDefault;
        Location = location;
        IsWholeFormCollection = isWholeFormCollection;
    }

    public string MemberName { get; }

    public ITypeSymbol Type { get; }

    public BindingSource Source { get; }

    public string SourceKey { get; }

    /// <summary>Set when the request type is a record / has a primary ctor.</summary>
    public int? CtorParameterIndex { get; }

    /// <summary>Set when the request type uses a parameterless ctor + property setters.</summary>
    public string? PropertyName { get; }

    /// <summary>
    /// True when the member carries a C# default value — a ctor-parameter default or a property
    /// initializer. ASP.NET's <c>[AsParameters]</c> binding ignores these for non-body verbs, so a
    /// non-nullable scalar with a default still binds as a required query parameter (WHTTP005).
    /// </summary>
    public bool HasClrDefault { get; }

    /// <summary>Declaration location of the member, for diagnostics. Null when unavailable.</summary>
    public Location? Location { get; }

    /// <summary>
    /// True for <c>IFormFileCollection</c> / <c>IFormCollection</c> members — ASP.NET binds the
    /// whole collection by type and rejects a <c>[FromForm(Name = ...)]</c>, so the generated
    /// parameter must be emitted bare (no name attribute). False for <c>IFormFile</c> and scalar
    /// <c>[FromForm]</c> fields, which bind by name.
    /// </summary>
    public bool IsWholeFormCollection { get; }
}

internal sealed class BindingPlan
{
    public BindingPlan(BindingShape shape, IReadOnlyList<BindingTarget> targets, bool usesPrimaryCtor)
    {
        Shape = shape;
        Targets = targets;
        UsesPrimaryCtor = usesPrimaryCtor;
    }

    public BindingShape Shape { get; }

    public IReadOnlyList<BindingTarget> Targets { get; }

    public bool UsesPrimaryCtor { get; }

    public bool HasBodyTargets => Targets.Any(t => t.Source == BindingSource.Body);

    public bool HasFormTargets => Targets.Any(t => t.Source == BindingSource.Form);
}

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Warp.Http.SourceGenerator.Emitters;

/// <summary>
/// Builds a <see cref="BindingPlan"/> for a request type. The plan's <see cref="BindingPlan.Shape"/>
/// drives how <see cref="DelegateEmitter"/> emits the lambda parameter list.
/// </summary>
internal static class BindingEmitter
{
    private const string FromRouteAttributeMetadataName = "Microsoft.AspNetCore.Mvc.FromRouteAttribute";
    private const string FromQueryAttributeMetadataName = "Microsoft.AspNetCore.Mvc.FromQueryAttribute";
    private const string FromHeaderAttributeMetadataName = "Microsoft.AspNetCore.Mvc.FromHeaderAttribute";
    private const string FromBodyAttributeMetadataName = "Microsoft.AspNetCore.Mvc.FromBodyAttribute";

    public static BindingPlan Build(Compilation compilation, INamedTypeSymbol requestType, string method)
    {
        var fromRoute = compilation.GetTypeByMetadataName(FromRouteAttributeMetadataName);
        var fromQuery = compilation.GetTypeByMetadataName(FromQueryAttributeMetadataName);
        var fromHeader = compilation.GetTypeByMetadataName(FromHeaderAttributeMetadataName);
        var fromBody = compilation.GetTypeByMetadataName(FromBodyAttributeMetadataName);

        var isBodyVerb = IsBodyVerb(method);

        var (usesPrimaryCtor, members) = EnumerateMembers(requestType);

        // Resolve a binding source for every member; if any are unattributed and the verb is
        // a body verb, treat them as body-default. Then categorize the plan shape.
        var targets = new List<BindingTarget>(members.Count);
        var sawAnyAttribute = false;
        var sawBody = false;

        foreach (var (memberName, memberType, ctorIndex, propertyName, attributedSymbol) in members)
        {
            var attr = ResolveAttribute(attributedSymbol, fromRoute, fromQuery, fromHeader, fromBody);
            if (attr is not null)
            {
                sawAnyAttribute = true;
            }

            BindingSource source;
            string key;
            switch (attr?.kind)
            {
                case BindingSource.Route:
                    source = BindingSource.Route;
                    key = attr.Value.name ?? memberName;
                    break;
                case BindingSource.Query:
                    source = BindingSource.Query;
                    key = attr.Value.name ?? memberName;
                    break;
                case BindingSource.Header:
                    source = BindingSource.Header;
                    key = attr.Value.name ?? memberName;
                    break;
                case BindingSource.Body:
                    source = BindingSource.Body;
                    key = memberName;
                    sawBody = true;
                    break;
                default:
                    source = isBodyVerb ? BindingSource.Body : BindingSource.Query;
                    key = memberName;
                    if (source == BindingSource.Body)
                    {
                        sawBody = true;
                    }

                    break;
            }

            targets.Add(new BindingTarget(
                memberName,
                memberType,
                source,
                key,
                ctorIndex,
                propertyName,
                HasClrDefault(attributedSymbol),
                attributedSymbol.Locations.FirstOrDefault()));
        }

        // Whole-body default: body verb, no attributes anywhere — Minimal API binds TRequest from body.
        if (isBodyVerb && !sawAnyAttribute)
        {
            return new BindingPlan(BindingShape.WholeBody, targets, usesPrimaryCtor);
        }

        // No body parts → safe to use [AsParameters] on TRequest directly. ASP.NET decomposes
        // per attribute (with route-token name auto-binding for any unattributed member on a
        // non-body verb).
        if (!sawBody)
        {
            return new BindingPlan(BindingShape.AsParameters, targets, usesPrimaryCtor);
        }

        // Mixed: body + route/query/header. [AsParameters] doesn't support [FromBody] members,
        // so the generator emits explicit lambda parameters per source and constructs TRequest.
        return new BindingPlan(BindingShape.Mixed, targets, usesPrimaryCtor);
    }

    private static (bool UsesPrimaryCtor, IReadOnlyList<MemberInfo> Members) EnumerateMembers(INamedTypeSymbol requestType)
    {
        var primaryCtor = requestType.InstanceConstructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && c.Parameters.Length > 0)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();

        var hasParameterlessCtor = requestType.InstanceConstructors
            .Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);

        // Prefer the parameterless-ctor + properties shape when available — properties carry
        // attributes more reliably than primary-ctor parameters with [AsParameters].
        if (hasParameterlessCtor)
        {
            var properties = requestType.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(p => p.SetMethod is not null
                    && p.SetMethod.DeclaredAccessibility == Accessibility.Public
                    && p.GetMethod is not null
                    && !p.IsStatic)
                .Select(p => new MemberInfo(p.Name, p.Type, ctorIndex: null, propertyName: p.Name, attributedSymbol: p))
                .ToList();

            return (false, properties);
        }

        if (primaryCtor is null)
        {
            // Empty record — nothing to bind.
            return (true, []);
        }

        var ctorMembers = new List<MemberInfo>(primaryCtor.Parameters.Length);
        for (var i = 0; i < primaryCtor.Parameters.Length; i++)
        {
            var p = primaryCtor.Parameters[i];
            ctorMembers.Add(new MemberInfo(p.Name, p.Type, ctorIndex: i, propertyName: null, attributedSymbol: p));
        }

        return (true, ctorMembers);
    }

    private static (BindingSource kind, string? name)? ResolveAttribute(
        ISymbol member,
        INamedTypeSymbol? fromRoute,
        INamedTypeSymbol? fromQuery,
        INamedTypeSymbol? fromHeader,
        INamedTypeSymbol? fromBody)
    {
        foreach (var attr in member.GetAttributes())
        {
            if (attr.AttributeClass is null)
            {
                continue;
            }

            if (fromRoute is not null && SymbolEqualityComparer.Default.Equals(attr.AttributeClass, fromRoute))
            {
                return (BindingSource.Route, ReadName(attr));
            }

            if (fromQuery is not null && SymbolEqualityComparer.Default.Equals(attr.AttributeClass, fromQuery))
            {
                return (BindingSource.Query, ReadName(attr));
            }

            if (fromHeader is not null && SymbolEqualityComparer.Default.Equals(attr.AttributeClass, fromHeader))
            {
                return (BindingSource.Header, ReadName(attr));
            }

            if (fromBody is not null && SymbolEqualityComparer.Default.Equals(attr.AttributeClass, fromBody))
            {
                return (BindingSource.Body, null);
            }
        }

        return null;
    }

    private static string? ReadName(AttributeData attr)
    {
        return attr.NamedArguments
            .Where(p => string.Equals(p.Key, "Name", System.StringComparison.Ordinal))
            .Select(p => p.Value.Value as string)
            .FirstOrDefault();
    }

    private static bool IsBodyVerb(string method)
    {
        return string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PUT", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "PATCH", System.StringComparison.OrdinalIgnoreCase);
    }

    // True when the member carries a C# default: a ctor-parameter default (record positional
    // param) or a property initializer. ASP.NET's [AsParameters] binding ignores both on
    // non-body verbs — the value still binds as a required query parameter (WHTTP005).
    private static bool HasClrDefault(ISymbol member)
    {
        if (member is IParameterSymbol parameter)
        {
            return parameter.HasExplicitDefaultValue;
        }

        if (member is IPropertySymbol property)
        {
            return property.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax())
                .OfType<PropertyDeclarationSyntax>()
                .Any(p => p.Initializer is not null);
        }

        return false;
    }

    internal sealed class MemberInfo
    {
        public MemberInfo(string name, ITypeSymbol type, int? ctorIndex, string? propertyName, ISymbol attributedSymbol)
        {
            Name = name;
            Type = type;
            CtorIndex = ctorIndex;
            PropertyName = propertyName;
            AttributedSymbol = attributedSymbol;
        }

        public string Name { get; }

        public ITypeSymbol Type { get; }

        public int? CtorIndex { get; }

        public string? PropertyName { get; }

        public ISymbol AttributedSymbol { get; }

        public void Deconstruct(out string name, out ITypeSymbol type, out int? ctorIndex, out string? propertyName, out ISymbol attributedSymbol)
        {
            name = Name;
            type = Type;
            ctorIndex = CtorIndex;
            propertyName = PropertyName;
            attributedSymbol = AttributedSymbol;
        }
    }
}

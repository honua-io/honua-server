// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Spec.Domain;

public abstract record SpecNode(SourceSpan Span);

public abstract record SpecExpression(SourceSpan Span) : SpecNode(Span)
{
    public TypeRef? InferredType { get; init; }
}

#pragma warning disable CA1720 // Literal-payload field names intentionally mirror the primitive kind.
public sealed record LiteralNode(
    SourceSpan Span,
    SpecTypeKind Kind,
    string? String = null,
    double? Number = null,
    long? Integer = null,
    bool? Boolean = null,
    string? Unit = null) : SpecExpression(Span);
#pragma warning restore CA1720

public sealed record GeometryLiteral(
    SourceSpan Span,
    NetTopologySuite.Geometries.Geometry Geometry,
    string WellKnownText,
    string? Crs = null) : SpecExpression(Span);

public sealed record Cql2Expression(SourceSpan Span, string Cql2Text) : SpecExpression(Span);

public sealed record ReferenceNode(
    SourceSpan Span,
    string Root,
    ImmutableArray<string> Segments,
    string? Call = null) : SpecExpression(Span)
{
    public string Canonical =>
        Segments.IsDefaultOrEmpty
            ? (Call is null ? $"@{Root}" : $"@{Root}.{Call}()")
            : Call is null
                ? $"@{Root}.{string.Join('.', Segments)}"
                : $"@{Root}.{string.Join('.', Segments)}.{Call}()";
}

public sealed record ArrayLiteral(
    SourceSpan Span,
    ImmutableArray<SpecExpression> Items) : SpecExpression(Span);

public sealed record ObjectExpression(
    SourceSpan Span,
    ImmutableArray<ObjectField> Fields) : SpecExpression(Span);

public sealed record ObjectField(
    SourceSpan Span,
    SourceSpan KeySpan,
    string Key,
    SpecExpression Value);

public sealed record SourceBinding(
    SourceSpan Span,
    string Id,
    ObjectExpression Properties) : SpecNode(Span);

public sealed record ScopeClause(
    SourceSpan Span,
    ReferenceNode Target,
    SpecExpression? Where) : SpecNode(Span);

public sealed record ComputeStep(
    SourceSpan Span,
    string Id,
    string OperatorName,
    SourceSpan OperatorSpan,
    ObjectExpression? Inputs,
    ObjectExpression? Parameters) : SpecNode(Span);

public sealed record MapSpec(
    SourceSpan Span,
    ArrayLiteral? Layers,
    ObjectExpression? Viewport,
    ObjectExpression? Legend) : SpecNode(Span);

public sealed record OutputBinding(
    SourceSpan Span,
    string Id,
    SpecExpression Expression) : SpecNode(Span);

public sealed record SpecDocument(
    SourceSpan Span,
    string? Grammar,
    SourceSpan GrammarSpan,
    string? Kind,
    string? Title,
    ImmutableArray<SourceBinding> Sources,
    ImmutableArray<ScopeClause> Scopes,
    ImmutableArray<ComputeStep> Compute,
    MapSpec? Map,
    ImmutableArray<OutputBinding> Outputs,
    ImmutableDictionary<string, string> Comments) : SpecNode(Span);

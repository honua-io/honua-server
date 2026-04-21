// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Abstract base type for every node in the spec AST. Each concrete node
/// carries its <see cref="SourceSpan"/> so that diagnostics can back-reference
/// the originating text.
/// </summary>
public abstract record SpecNode(SourceSpan Span);

/// <summary>
/// Base type for expression nodes — literals, references, and operator-call
/// expressions. Tracks an optional <see cref="InferredType"/> populated by
/// the type checker.
/// </summary>
public abstract record SpecExpression(SourceSpan Span) : SpecNode(Span)
{
    /// <summary>
    /// Type inferred by the type checker. <c>null</c> until type checking has
    /// run; <see cref="TypeRef.Intrinsic(SpecTypeKind)"/> with
    /// <see cref="SpecTypeKind.Unknown"/> when the checker could not resolve it.
    /// </summary>
    public TypeRef? InferredType { get; init; }
}

/// <summary>
/// Primitive literal (string, number, integer, boolean, unit-carrying numeric
/// literals such as <c>5.km</c>, datetime, CRS).
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Kind">Which primitive kind this literal expresses.</param>
/// <param name="String">Raw string value for text literals; otherwise <c>null</c>.</param>
/// <param name="Number">Parsed numeric value for number/integer/distance/duration/area literals.</param>
/// <param name="Integer">Parsed integer value (only when <paramref name="Kind"/> is <see cref="SpecTypeKind.Integer"/>).</param>
/// <param name="Boolean">Parsed boolean value (only when <paramref name="Kind"/> is <see cref="SpecTypeKind.Boolean"/>).</param>
/// <param name="Unit">Unit suffix (<c>km</c>, <c>m</c>, <c>s</c>, <c>min</c>, <c>h</c>, <c>ha</c>, …) for distance/duration/area literals.</param>
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

/// <summary>
/// Geometry literal reusing the CQL2 WKT/BBOX parser path — the spec delegates
/// geometry lexing to <c>Cql2Parser</c> to avoid re-implementing WKT.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Geometry">NTS geometry.</param>
/// <param name="WellKnownText">Original WKT text (pre-canonicalization).</param>
/// <param name="Crs">Optional CRS identifier (<c>EPSG:xxxx</c>).</param>
public sealed record GeometryLiteral(
    SourceSpan Span,
    NetTopologySuite.Geometries.Geometry Geometry,
    string WellKnownText,
    string? Crs = null) : SpecExpression(Span);

/// <summary>
/// Embedded CQL2 expression inside a <c>where</c> clause. The raw CQL2
/// expression text is preserved; validation of the CQL2 expression itself is
/// delegated to <c>Cql2Parser</c> by the reference resolver.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Cql2Text">Raw CQL2 expression inside the <c>cql2(...)</c> call.</param>
public sealed record Cql2Expression(SourceSpan Span, string Cql2Text) : SpecExpression(Span);

/// <summary>
/// <c>@</c>-prefixed reference to a local binding (<c>@hospitals</c>) or a
/// dotted compute/output chain (<c>@compute.near_rivers.count()</c>).
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Root">Root identifier (e.g. <c>hospitals</c>, <c>compute</c>).</param>
/// <param name="Segments">Dotted segments after the root.</param>
/// <param name="Call">Optional call suffix (e.g. <c>count</c>) when the reference ends in <c>()</c>.</param>
public sealed record ReferenceNode(
    SourceSpan Span,
    string Root,
    ImmutableArray<string> Segments,
    string? Call = null) : SpecExpression(Span)
{
    /// <summary>
    /// Dotted, <c>@</c>-prefixed string used in diagnostic messages and canonical JSON.
    /// </summary>
    public string Canonical =>
        Segments.IsDefaultOrEmpty
            ? (Call is null ? $"@{Root}" : $"@{Root}.{Call}()")
            : Call is null
                ? $"@{Root}.{string.Join('.', Segments)}"
                : $"@{Root}.{string.Join('.', Segments)}.{Call}()";
}

/// <summary>
/// Ordered array literal (<c>[a, b, c]</c>) — used for map layer arrays.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Items">Ordered items.</param>
public sealed record ArrayLiteral(
    SourceSpan Span,
    ImmutableArray<SpecExpression> Items) : SpecExpression(Span);

/// <summary>
/// Keyed object expression (<c>{ key = value, … }</c>). Declaration order of
/// fields is preserved for text round-trips; canonical JSON emits fields in
/// alphabetical order.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Fields">Ordered key/value fields as parsed.</param>
public sealed record ObjectExpression(
    SourceSpan Span,
    ImmutableArray<ObjectField> Fields) : SpecExpression(Span);

/// <summary>
/// Single <c>key = value</c> field inside an <see cref="ObjectExpression"/>.
/// </summary>
/// <param name="Span">Source location spanning key to value.</param>
/// <param name="KeySpan">Span of the key identifier.</param>
/// <param name="Key">Field name.</param>
/// <param name="Value">Field value.</param>
public sealed record ObjectField(
    SourceSpan Span,
    SourceSpan KeySpan,
    string Key,
    SpecExpression Value);

/// <summary>
/// <c>source &lt;id&gt; { type = "…", ref = "…", … }</c> binding.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Id">Local identifier.</param>
/// <param name="Properties">Configuration object.</param>
public sealed record SourceBinding(
    SourceSpan Span,
    string Id,
    ObjectExpression Properties) : SpecNode(Span);

/// <summary>
/// <c>scope { target = @id, where = cql2(…) }</c> clause.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Target">Reference to the source/compute the clause scopes.</param>
/// <param name="Where">Optional filter expression (typically a <see cref="Cql2Expression"/>).</param>
public sealed record ScopeClause(
    SourceSpan Span,
    ReferenceNode Target,
    SpecExpression? Where) : SpecNode(Span);

/// <summary>
/// <c>compute &lt;id&gt; { op = …, inputs = { … }, params = { … } }</c> step.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Id">Local identifier.</param>
/// <param name="OperatorName">Operator keyword (e.g. <c>spatial_join</c>).</param>
/// <param name="OperatorSpan">Span of the operator identifier.</param>
/// <param name="Inputs">Inputs object (<c>left = @a, right = @b</c>).</param>
/// <param name="Parameters">Parameters object.</param>
public sealed record ComputeStep(
    SourceSpan Span,
    string Id,
    string OperatorName,
    SourceSpan OperatorSpan,
    ObjectExpression? Inputs,
    ObjectExpression? Parameters) : SpecNode(Span);

/// <summary>
/// <c>map { layers = [...], viewport = {...}, legend = {...} }</c> section.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Layers">Ordered layer array.</param>
/// <param name="Viewport">Optional viewport object.</param>
/// <param name="Legend">Optional legend object.</param>
public sealed record MapSpec(
    SourceSpan Span,
    ArrayLiteral? Layers,
    ObjectExpression? Viewport,
    ObjectExpression? Legend) : SpecNode(Span);

/// <summary>
/// <c>output &lt;id&gt; { expr = ... }</c> binding.
/// </summary>
/// <param name="Span">Source location.</param>
/// <param name="Id">Local identifier.</param>
/// <param name="Expression">Value expression (typically a reference or operator call).</param>
public sealed record OutputBinding(
    SourceSpan Span,
    string Id,
    SpecExpression Expression) : SpecNode(Span);

/// <summary>
/// Root AST node for a parsed spec document.
/// </summary>
/// <param name="Span">Full document span.</param>
/// <param name="Grammar">Declared grammar version (e.g. <c>v1.0</c>).</param>
/// <param name="GrammarSpan">Span of the grammar version literal.</param>
/// <param name="Kind">Declared document kind (<c>analysis</c>, <c>map</c>, …).</param>
/// <param name="Title">Optional human title.</param>
/// <param name="Sources">Declared sources in declaration order.</param>
/// <param name="Scopes">Declared scope clauses in declaration order.</param>
/// <param name="Compute">Declared compute steps in declaration order.</param>
/// <param name="Map">Map section, if any.</param>
/// <param name="Outputs">Declared outputs in declaration order.</param>
/// <param name="Comments">Map from JSON-Pointer path to comment text (block-level comments).</param>
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

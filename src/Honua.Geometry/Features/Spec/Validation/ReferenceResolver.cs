// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Validation;

/// <summary>
/// First validator pass — resolves every <c>@</c>-reference to either a
/// local binding (<c>sources</c>/<c>compute</c>/<c>outputs</c>) or a catalog
/// entry (via <see cref="ISpecCatalogSnapshot"/>). Emits
/// <see cref="SpecDiagnosticCode.UnknownReference"/> for targets that cannot
/// be bound, <see cref="SpecDiagnosticCode.DuplicateIdentifier"/> for
/// duplicate ids, and <see cref="SpecDiagnosticCode.CatalogUnavailable"/>
/// when the snapshot is the empty one.
/// </summary>
internal sealed class ReferenceResolver
{
    private readonly SpecDocument _document;
    private readonly ISpecCatalogSnapshot _catalog;
    private readonly Dictionary<string, (TypeRef Type, SourceSpan Span)> _locals = new(StringComparer.Ordinal);
    private readonly List<SpecDiagnostic> _diagnostics = [];
    private bool _catalogWarned;

    /// <summary>
    /// Creates a resolver bound to <paramref name="document"/> and
    /// <paramref name="catalog"/>.
    /// </summary>
    public ReferenceResolver(SpecDocument document, ISpecCatalogSnapshot catalog)
    {
        _document = document;
        _catalog = catalog;
    }

    /// <summary>
    /// Gets the diagnostics produced during <see cref="Resolve"/>.
    /// </summary>
    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Gets the local binding table populated by <see cref="Resolve"/>.
    /// </summary>
    public IReadOnlyDictionary<string, TypeRef> Locals =>
        _locals.ToDictionary(kv => kv.Key, kv => kv.Value.Type, StringComparer.Ordinal);

    /// <summary>
    /// Walks the document, binding local ids and resolving external sources.
    /// </summary>
    public void Resolve()
    {
        _diagnostics.Clear();
        _locals.Clear();
        _catalogWarned = false;

        foreach (var source in _document.Sources)
        {
            if (_locals.ContainsKey(source.Id))
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.DuplicateIdentifier,
                    $"Duplicate source id '{source.Id}'.",
                    source.Span));
                continue;
            }

            var type = ResolveSource(source);
            _locals[source.Id] = (type, source.Span);
        }

        foreach (var step in _document.Compute)
        {
            if (_locals.ContainsKey(step.Id))
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.DuplicateIdentifier,
                    $"Duplicate compute id '{step.Id}' — shadows an earlier source or compute id.",
                    step.Span));
                continue;
            }

            _locals[step.Id] = (TypeRef.Intrinsic(SpecTypeKind.Dataset), step.Span);
        }

        foreach (var output in _document.Outputs)
        {
            if (_locals.ContainsKey(output.Id))
            {
                _diagnostics.Add(SpecDiagnostic.Error(
                    SpecDiagnosticCode.DuplicateIdentifier,
                    $"Duplicate output id '{output.Id}' — shadows an earlier source, compute, or output id.",
                    output.Span));
            }
            else
            {
                _locals[output.Id] = (TypeRef.Intrinsic(SpecTypeKind.Unknown), output.Span);
            }
        }

        foreach (var scope in _document.Scopes)
        {
            ValidateReference(scope.Target, scope.Span);
        }

        foreach (var step in _document.Compute)
        {
            if (step.Inputs is not null)
            {
                ValidateReferencesInExpression(step.Inputs);
            }

            if (step.Parameters is not null)
            {
                ValidateReferencesInExpression(step.Parameters);
            }
        }

        if (_document.Map is not null)
        {
            if (_document.Map.Layers is not null)
            {
                ValidateReferencesInExpression(_document.Map.Layers);
            }

            if (_document.Map.Viewport is not null)
            {
                ValidateReferencesInExpression(_document.Map.Viewport);
            }
        }

        foreach (var output in _document.Outputs)
        {
            ValidateReferencesInExpression(output.Expression);
        }
    }

    private TypeRef ResolveSource(SourceBinding source)
    {
        var resolved = _catalog.ResolveSource(source);
        if (resolved is null)
        {
            if (ReferenceEquals(_catalog, StaticSpecCatalogSnapshot.Empty) && !_catalogWarned)
            {
                _diagnostics.Add(SpecDiagnostic.Warning(
                    SpecDiagnosticCode.CatalogUnavailable,
                    "Catalog snapshot is empty; external '@' references will be validated structurally only.",
                    source.Span));
                _catalogWarned = true;
            }

            resolved = InferStructuralType(source);
        }

        return resolved;
    }

    private static TypeRef InferStructuralType(SourceBinding source)
    {
        foreach (var field in source.Properties.Fields)
        {
            if (field.Key == "type" && field.Value is LiteralNode literal && literal.Kind == SpecTypeKind.String)
            {
                return literal.String switch
                {
                    "raster" or "cog" => TypeRef.Intrinsic(SpecTypeKind.Raster),
                    "layer" or "service" or "stac" or "parquet" => TypeRef.Intrinsic(SpecTypeKind.Dataset),
                    _ => TypeRef.Intrinsic(SpecTypeKind.Dataset)
                };
            }
        }

        return TypeRef.Intrinsic(SpecTypeKind.Dataset);
    }

    private void ValidateReferencesInExpression(SpecExpression expression)
    {
        switch (expression)
        {
            case ReferenceNode reference:
                ValidateReference(reference, reference.Span);
                break;

            case ArrayLiteral array:
                foreach (var item in array.Items)
                {
                    ValidateReferencesInExpression(item);
                }

                break;

            case ObjectExpression obj:
                foreach (var field in obj.Fields)
                {
                    ValidateReferencesInExpression(field.Value);
                }

                break;

            default:
                break;
        }
    }

    private void ValidateReference(ReferenceNode reference, SourceSpan fallback)
    {
        if (_locals.ContainsKey(reference.Root))
        {
            return;
        }

        _diagnostics.Add(SpecDiagnostic.Error(
            SpecDiagnosticCode.UnknownReference,
            $"Reference '{reference.Canonical}' does not match any declared source, compute, or output.",
            reference.Span.HasLocation ? reference.Span : fallback));
    }
}

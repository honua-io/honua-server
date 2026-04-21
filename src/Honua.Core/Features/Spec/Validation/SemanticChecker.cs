// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;

namespace Honua.Core.Features.Spec.Validation;

/// <summary>
/// Third validator pass — semantic guards that depend on the other passes
/// having succeeded. Enforces projected-CRS rules on distance/area literals,
/// grammar-version compatibility, and operator-capability drift.
/// </summary>
internal sealed class SemanticChecker
{
    private static readonly FrozenSet<string> _geographicCrs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EPSG:4326", "CRS84", "OGC:CRS84" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SpecDocument _document;
    private readonly IOperatorCatalog _operators;
    private readonly List<SpecDiagnostic> _diagnostics = [];

    /// <summary>
    /// Creates a semantic checker.
    /// </summary>
    public SemanticChecker(SpecDocument document, IOperatorCatalog operators)
    {
        _document = document;
        _operators = operators;
    }

    /// <summary>Diagnostics produced by <see cref="Check"/>.</summary>
    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Runs the semantic-level checks.</summary>
    public void Check()
    {
        _diagnostics.Clear();

        CheckGrammarVersion();

        foreach (var step in _document.Compute)
        {
            var signature = _operators.Lookup(step.OperatorName);
            if (signature is null || !signature.CrsSensitive || step.Parameters is null)
            {
                continue;
            }

            string? declaredCrs = null;
            foreach (var field in step.Parameters.Fields)
            {
                if (field.Key == "crs" &&
                    field.Value is LiteralNode literal &&
                    literal.Kind == SpecTypeKind.String)
                {
                    declaredCrs = literal.String;
                }
            }

            foreach (var field in step.Parameters.Fields)
            {
                if (field.Value is LiteralNode unitLiteral &&
                    unitLiteral.Kind is SpecTypeKind.Distance or SpecTypeKind.Area)
                {
                    if (declaredCrs is null)
                    {
                        _diagnostics.Add(SpecDiagnostic.Warning(
                            SpecDiagnosticCode.CrsUnitMismatch,
                            $"Operator '{signature.Name}' uses a {unitLiteral.Kind.ToString().ToLowerInvariant()} parameter '{field.Key}' but no crs was declared; results assume meters on a projected CRS.",
                            field.Value.Span));
                    }
                    else if (_geographicCrs.Contains(declaredCrs))
                    {
                        _diagnostics.Add(SpecDiagnostic.Error(
                            SpecDiagnosticCode.CrsUnitMismatch,
                            $"Operator '{signature.Name}' parameter '{field.Key}' is a {unitLiteral.Kind.ToString().ToLowerInvariant()} literal but crs '{declaredCrs}' is geographic; reproject to a projected CRS or drop the unit.",
                            field.Value.Span));
                    }
                }
            }
        }
    }

    private void CheckGrammarVersion()
    {
        if (string.IsNullOrEmpty(_document.Grammar))
        {
            _diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.UnsupportedGrammarVersion,
                $"grammar directive is required and must declare a supported version (current: {SpecGrammarVersion.Current}).",
                _document.GrammarSpan.HasLocation ? _document.GrammarSpan : _document.Span));
            return;
        }

        if (!SpecGrammarVersion.TryParse(_document.Grammar, out var major, out var minor))
        {
            _diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.UnsupportedGrammarVersion,
                $"grammar '{_document.Grammar}' is not in the form vMAJOR.MINOR.",
                _document.GrammarSpan));
            return;
        }

        if (major != SpecGrammarVersion.CurrentMajor)
        {
            _diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.UnsupportedGrammarVersion,
                $"Spec requires grammar major v{major} but this server supports v{SpecGrammarVersion.CurrentMajor} (current: {SpecGrammarVersion.Current}).",
                _document.GrammarSpan));
            return;
        }

        if (minor > SpecGrammarVersion.CurrentMinor)
        {
            _diagnostics.Add(SpecDiagnostic.Error(
                SpecDiagnosticCode.UnsupportedGrammarVersion,
                $"Spec requires grammar v{major}.{minor} but this server supports up to {SpecGrammarVersion.Current}.",
                _document.GrammarSpan));
        }
    }
}

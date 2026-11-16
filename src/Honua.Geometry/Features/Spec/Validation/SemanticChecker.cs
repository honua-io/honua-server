// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;

namespace Honua.Core.Features.Spec.Validation;

internal sealed class SemanticChecker
{
    private static readonly FrozenSet<string> _geographicCrs =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EPSG:4326", "CRS84", "OGC:CRS84" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly SpecDocument _document;
    private readonly IOperatorCatalog _operators;
    private readonly List<SpecDiagnostic> _diagnostics = [];

    public SemanticChecker(SpecDocument document, IOperatorCatalog operators)
    {
        _document = document;
        _operators = operators;
    }

    public IReadOnlyList<SpecDiagnostic> Diagnostics => _diagnostics;

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

            // Last matching "crs" field wins, mirroring how duplicate object keys are
            // resolved elsewhere in the canonical form.
            var declaredCrs = step.Parameters.Fields
                .Where(field => field.Key == "crs" && field.Value is LiteralNode { Kind: SpecTypeKind.String })
                .Select(field => ((LiteralNode)field.Value).String)
                .LastOrDefault();

            foreach (var field in step.Parameters.Fields.Where(
                field => field.Value is LiteralNode { Kind: SpecTypeKind.Distance or SpecTypeKind.Area }))
            {
                var unitLiteral = (LiteralNode)field.Value;
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

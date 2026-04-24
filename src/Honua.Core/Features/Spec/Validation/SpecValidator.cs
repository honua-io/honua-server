// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;

namespace Honua.Core.Features.Spec.Validation;

/// <summary>
/// Orchestrates the three validator passes (resolve → type-check →
/// semantic-check) and returns the accumulated diagnostics. Exceptions are
/// reserved for internal invariant violations.
/// </summary>
public sealed class SpecValidator : ISpecValidator
{
    /// <summary>
    /// Activity source used for spec-validation telemetry. Consumers that
    /// listen on this source see per-pass durations plus diagnostic counts.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new("Honua.Spec.Validation");

    private readonly IOperatorCatalog _operators;

    /// <summary>
    /// Creates a validator over the given operator catalog.
    /// </summary>
    /// <param name="operators">Operator catalog.</param>
    public SpecValidator(IOperatorCatalog operators)
    {
        _operators = operators;
    }

    /// <inheritdoc />
    public SpecValidationResult Validate(SpecDocument document, ISpecCatalogSnapshot? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var activity = ActivitySource.StartActivity("spec.validate");
        catalog ??= StaticSpecCatalogSnapshot.Empty;

        var diagnostics = new List<SpecDiagnostic>();

        var resolver = new ReferenceResolver(document, catalog);
        resolver.Resolve();
        diagnostics.AddRange(resolver.Diagnostics);
        activity?.SetTag("spec.resolver.diagnostics", resolver.Diagnostics.Count);

        if (!HasError(resolver.Diagnostics))
        {
            var types = new TypeChecker(document, _operators, resolver.Locals);
            types.Check();
            diagnostics.AddRange(types.Diagnostics);
            activity?.SetTag("spec.type.diagnostics", types.Diagnostics.Count);
        }

        var semantic = new SemanticChecker(document, _operators);
        semantic.Check();
        diagnostics.AddRange(semantic.Diagnostics);
        activity?.SetTag("spec.semantic.diagnostics", semantic.Diagnostics.Count);

        activity?.SetTag("spec.total.diagnostics", diagnostics.Count);
        return new SpecValidationResult(diagnostics);
    }

    private static bool HasError(IReadOnlyList<SpecDiagnostic> diagnostics)
    {
        foreach (var d in diagnostics)
        {
            if (d.Severity == SpecDiagnosticSeverity.Error)
            {
                return true;
            }
        }

        return false;
    }
}

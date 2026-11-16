// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Operators;

namespace Honua.Core.Features.Spec.Validation;

public sealed class SpecValidator : ISpecValidator
{
    public static readonly ActivitySource ActivitySource = new("Honua.Spec.Validation");

    private readonly IOperatorCatalog _operators;

    public SpecValidator(IOperatorCatalog operators)
    {
        _operators = operators;
    }

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

    private static bool HasError(IReadOnlyList<SpecDiagnostic> diagnostics) =>
        diagnostics.Any(d => d.Severity == SpecDiagnosticSeverity.Error);
}

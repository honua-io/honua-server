// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed record SpecParseResult(
    SpecDocument? Document,
    IReadOnlyList<SpecDiagnostic> Diagnostics)
{
    public bool IsSuccess =>
        Document is not null && !Diagnostics.Any(d => d.Severity == SpecDiagnosticSeverity.Error);
}

public sealed record SpecValidationResult(IReadOnlyList<SpecDiagnostic> Diagnostics)
{
    public bool IsValid => !Diagnostics.Any(d => d.Severity == SpecDiagnosticSeverity.Error);
}

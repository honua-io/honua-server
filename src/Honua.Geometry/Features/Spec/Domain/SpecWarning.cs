// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed record SpecWarning
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public SpecDiagnosticSeverity Severity { get; init; } = SpecDiagnosticSeverity.Warning;

    public string? NodeId { get; init; }

    public string? Remedy { get; init; }
}

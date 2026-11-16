// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record GeoprocessingError
{
    public required GeoprocessingErrorKind Kind { get; init; }

    public required string Message { get; init; }

    public string? StepId { get; init; }

    public IReadOnlyList<GeoprocessingValidationFailure>? Violations { get; init; }
}

public sealed record GeoprocessingValidationFailure
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? FieldPath { get; init; }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record PlanValidationResult
{
    public required bool IsExecutable { get; init; }

    public bool RequiresApproval { get; init; }

    public IReadOnlyList<GeoprocessingValidationFailure> Violations { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record DryRunResult
{
    public double EstimatedDurationSeconds { get; init; }

    public bool DurationEstimateAvailable { get; init; }

    public IReadOnlyList<ArtifactKind> EstimatedArtifacts { get; init; } = [];

    public IReadOnlyList<string> SideEffects { get; init; } = [];
}

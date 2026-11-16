// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record AnalysisIntent
{
    public required string IntentId { get; init; }

    public required string Goal { get; init; }

    public string? Mode { get; init; }

    public IReadOnlyList<ArtifactKind> RequestedOutputs { get; init; } = [];

    public IntentConstraints? Constraints { get; init; }

    public IReadOnlyList<string> Inputs { get; init; } = [];

    public AssumptionPolicy AssumptionPolicy { get; init; } = AssumptionPolicy.AskWhenMaterial;
}

public sealed record IntentConstraints
{
    public string? AreaOfInterest { get; init; }

    public int? SpatialReferenceId { get; init; }

    public DateTimeOffset? TimeWindowStart { get; init; }

    public DateTimeOffset? TimeWindowEnd { get; init; }

    public string? Units { get; init; }
}

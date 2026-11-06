// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

internal sealed record GpPlan
{
    public required string ProcessId { get; init; }

    public required string Title { get; init; }

    public required string Category { get; init; }

    public required string RuntimeProfile { get; init; }

    public bool IsValid => Errors.Count == 0;

    public required IReadOnlyList<string> Errors { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required IReadOnlyList<GpPlanStep> Steps { get; init; }

    public required IReadOnlyList<ArtifactKind> Outputs { get; init; }

    public required GpSizeEstimate Estimate { get; init; }
}

internal sealed record GpPlanStep
{
    public required string StepId { get; init; }

    public required string ProcessId { get; init; }

    public required IReadOnlyList<string> DependsOn { get; init; }

    public required IReadOnlyList<GpResolvedParam> Parameters { get; init; }
}

internal sealed record GpResolvedParam
{
    public required string Name { get; init; }

    public required ProcessParameterValueType ValueType { get; init; }

    public required bool Required { get; init; }

    public required string? DisplayValue { get; init; }

    public required GpParamSource Source { get; init; }
}

internal enum GpParamSource
{
    Caller,

    Default,

    Unset,
}

internal sealed record GpSizeEstimate
{
    public required long InputBytes { get; init; }

    public required long? EstimatedOutputBytes { get; init; }

    public required long MaxArtifactBytes { get; init; }

    public required string Basis { get; init; }

    public bool ExceedsCap =>
        EstimatedOutputBytes is { } bytes && bytes > MaxArtifactBytes;

    public required bool LongRunning { get; init; }
}

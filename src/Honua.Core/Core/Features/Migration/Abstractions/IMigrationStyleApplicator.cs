// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Abstractions;

public interface IMigrationStyleApplicator
{
    Task<MigrationStyleApplyOutcome> ApplyAsync(
        MigrationLiveStyleApplyRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record MigrationLiveStyleApplyRequest
{
    public required string TargetStyleId { get; init; }

    public required string Title { get; init; }

    public string? MapLibreLayersJson { get; init; }

    public required string ReviewDisposition { get; init; }

    public IReadOnlyList<MigrationStyleLayerTarget> LayerTargets { get; init; } = [];
}

public sealed record MigrationStyleLayerTarget(int LayerId, int Ordinal);

public enum MigrationStyleApplyOutcome
{
    Applied,

    AlreadyApplied,

    SkippedManualReview,

    SkippedConflict,

    SkippedNoPublishedLayers
}

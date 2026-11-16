// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record AnalysisResultPackage
{
    public required string ResultPackageId { get; init; }

    public required GeoprocessingWorkflowStatus Status { get; init; }

    public required ResultSummary Summary { get; init; }

    public IReadOnlyList<string> Assumptions { get; init; } = [];

    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    public IReadOnlyList<WorkspaceRef> WorkspaceRefs { get; init; } = [];

    public string? MapPackageId { get; init; }

    public string? AppPackageId { get; init; }

    public required ProvenanceRecord Provenance { get; init; }

    public IReadOnlyList<GeoprocessingError> Errors { get; init; } = [];

    public static AnalysisResultPackage CreateCompleted(
        string resultPackageId,
        ResultSummary summary,
        IReadOnlyList<ArtifactRef> artifacts,
        IReadOnlyList<WorkspaceRef> workspaceRefs,
        ProvenanceRecord provenance,
        IReadOnlyList<string>? assumptions = null,
        string? mapPackageId = null,
        string? appPackageId = null)
        => new()
        {
            ResultPackageId = resultPackageId,
            Status = GeoprocessingWorkflowStatus.Completed,
            Summary = summary,
            Artifacts = artifacts,
            WorkspaceRefs = workspaceRefs,
            Provenance = provenance,
            Assumptions = assumptions ?? [],
            MapPackageId = mapPackageId,
            AppPackageId = appPackageId
        };

    public static AnalysisResultPackage CreateFailed(
        string resultPackageId,
        ResultSummary summary,
        IReadOnlyList<GeoprocessingError> errors,
        ProvenanceRecord provenance)
        => new()
        {
            ResultPackageId = resultPackageId,
            Status = GeoprocessingWorkflowStatus.Failed,
            Summary = summary,
            Errors = errors,
            Provenance = provenance
        };
}

public sealed record ResultSummary
{
    public required string Title { get; init; }

    public string? Description { get; init; }
}

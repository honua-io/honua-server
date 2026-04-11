// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Final result envelope for a geoprocessing workflow, containing all artifacts, workspace
/// references, provenance, and error information produced by the execution.
/// </summary>
public sealed record AnalysisResultPackage
{
    /// <summary>
    /// Unique identifier for this result package.
    /// </summary>
    public required string ResultPackageId { get; init; }

    /// <summary>
    /// Terminal workflow status of the operation that produced this package.
    /// </summary>
    public required GeoprocessingWorkflowStatus Status { get; init; }

    /// <summary>
    /// Human-readable summary of the result.
    /// </summary>
    public required ResultSummary Summary { get; init; }

    /// <summary>
    /// Assumptions made during workflow compilation and execution.
    /// </summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>
    /// Output artifacts produced by the workflow.
    /// </summary>
    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    /// <summary>
    /// Workspace references used or created during the workflow.
    /// </summary>
    public IReadOnlyList<WorkspaceRef> WorkspaceRefs { get; init; } = [];

    /// <summary>
    /// Identifier of the composed map package, when applicable. Deferred: MapPackage type defined in #730.
    /// </summary>
    public string? MapPackageId { get; init; }

    /// <summary>
    /// Identifier of the application package, when applicable. Deferred: AppPackage type defined in #731.
    /// </summary>
    public string? AppPackageId { get; init; }

    /// <summary>
    /// Audit trail for this result.
    /// </summary>
    public required ProvenanceRecord Provenance { get; init; }

    /// <summary>
    /// Errors encountered during the workflow.
    /// </summary>
    public IReadOnlyList<GeoprocessingError> Errors { get; init; } = [];

    /// <summary>
    /// Creates a completed result package with the given artifacts and provenance.
    /// </summary>
    public static AnalysisResultPackage CreateCompleted(
        string resultPackageId,
        ResultSummary summary,
        IReadOnlyList<ArtifactRef> artifacts,
        IReadOnlyList<WorkspaceRef> workspaceRefs,
        ProvenanceRecord provenance,
        IReadOnlyList<string>? assumptions = null)
        => new()
        {
            ResultPackageId = resultPackageId,
            Status = GeoprocessingWorkflowStatus.Completed,
            Summary = summary,
            Artifacts = artifacts,
            WorkspaceRefs = workspaceRefs,
            Provenance = provenance,
            Assumptions = assumptions ?? []
        };

    /// <summary>
    /// Creates a failed result package with the given errors and provenance.
    /// </summary>
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

/// <summary>
/// Human-readable summary of a geoprocessing result.
/// </summary>
public sealed record ResultSummary
{
    /// <summary>
    /// Title of the result.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Optional detailed description of the result.
    /// </summary>
    public string? Description { get; init; }
}

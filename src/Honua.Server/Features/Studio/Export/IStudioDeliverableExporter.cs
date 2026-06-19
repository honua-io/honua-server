// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Export;

/// <summary>
/// Outcome categories for a Studio deliverable export request.
/// </summary>
public enum StudioDeliverableExportStatus
{
    /// <summary>The deliverable was rendered successfully.</summary>
    Succeeded,

    /// <summary>No content item / version matched the request.</summary>
    NotFound,

    /// <summary>The requested kind did not match the resolved package family.</summary>
    KindMismatch,
}

/// <summary>
/// Result of a Studio deliverable export request.
/// </summary>
public sealed record StudioDeliverableExportResult
{
    /// <summary>Export outcome.</summary>
    public required StudioDeliverableExportStatus Status { get; init; }

    /// <summary>The rendered artifact when <see cref="Status"/> is <see cref="StudioDeliverableExportStatus.Succeeded"/>.</summary>
    public StudioDeliverableArtifact? Artifact { get; init; }

    /// <summary>Optional stored share-artifact URL when the artifact was persisted to cloud storage.</summary>
    public string? ArtifactUrl { get; init; }

    /// <summary>Human-readable detail for non-success outcomes.</summary>
    public string? Detail { get; init; }

    /// <summary>Creates a success result.</summary>
    public static StudioDeliverableExportResult CreateSuccess(StudioDeliverableArtifact artifact, string? artifactUrl = null)
        => new() { Status = StudioDeliverableExportStatus.Succeeded, Artifact = artifact, ArtifactUrl = artifactUrl };

    /// <summary>Creates a not-found result.</summary>
    public static StudioDeliverableExportResult CreateNotFound(string detail)
        => new() { Status = StudioDeliverableExportStatus.NotFound, Detail = detail };

    /// <summary>Creates a kind-mismatch result.</summary>
    public static StudioDeliverableExportResult CreateKindMismatch(string detail)
        => new() { Status = StudioDeliverableExportStatus.KindMismatch, Detail = detail };
}

/// <summary>
/// Renders a Studio content item (map, dashboard, or report) into a shareable PDF/PNG deliverable.
/// </summary>
public interface IStudioDeliverableExporter
{
    /// <summary>
    /// Exports the latest (or specified) version of a Studio content item as a deliverable artifact.
    /// </summary>
    /// <param name="kind">Requested deliverable kind (map, dashboard, or report).</param>
    /// <param name="itemId">Studio content item identifier.</param>
    /// <param name="format">Output format (PDF or PNG).</param>
    /// <param name="versionId">Optional explicit version; defaults to the latest version of the item.</param>
    /// <param name="store">When true, persist the artifact via cloud storage and return a share URL.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<StudioDeliverableExportResult> ExportAsync(
        StudioPackageFamily kind,
        Guid itemId,
        StudioDeliverableFormat format,
        Guid? versionId = null,
        bool store = false,
        CancellationToken cancellationToken = default);
}

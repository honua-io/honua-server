// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Export;

public enum StudioDeliverableExportStatus
{
    Succeeded,

    NotFound,

    KindMismatch,

    // A missing typeface is refused rather than shipping a blank artifact.
    RenderUnavailable,
}

public sealed record StudioDeliverableExportResult
{
    public required StudioDeliverableExportStatus Status { get; init; }

    public StudioDeliverableArtifact? Artifact { get; init; }

    public string? ArtifactUrl { get; init; }

    public string? Detail { get; init; }

    public string? Code { get; init; }

    public static StudioDeliverableExportResult CreateSuccess(StudioDeliverableArtifact artifact, string? artifactUrl = null)
        => new() { Status = StudioDeliverableExportStatus.Succeeded, Artifact = artifact, ArtifactUrl = artifactUrl };

    public static StudioDeliverableExportResult CreateNotFound(string detail)
        => new() { Status = StudioDeliverableExportStatus.NotFound, Detail = detail };

    public static StudioDeliverableExportResult CreateKindMismatch(string detail)
        => new() { Status = StudioDeliverableExportStatus.KindMismatch, Detail = detail };

    public static StudioDeliverableExportResult CreateRenderUnavailable(string detail, string code)
        => new() { Status = StudioDeliverableExportStatus.RenderUnavailable, Detail = detail, Code = code };
}

public interface IStudioDeliverableExporter
{
    Task<StudioDeliverableExportResult> ExportAsync(
        StudioPackageFamily kind,
        Guid itemId,
        StudioDeliverableFormat format,
        Guid? versionId = null,
        bool store = false,
        CancellationToken cancellationToken = default);
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

public sealed class SceneArchiveIngestResponse
{
    public string SceneId { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public string I3sUrl { get; set; } = string.Empty;

    public string TilesetFileName { get; set; } = "tileset.json";

    public string TilesetVersion { get; set; } = "1.1";

    public int FileCount { get; set; }

    public long TotalBytes { get; set; }

    public SceneExtentDto? Extent { get; set; }

    public string[] Warnings { get; set; } = [];
}

public sealed class SceneModelIngestResponse
{
    public string SceneId { get; set; } = string.Empty;

    public string TilesetUrl { get; set; } = string.Empty;

    public string I3sUrl { get; set; } = string.Empty;

    public string SourceFormat { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public double[] BoundingRegionDegrees { get; set; } = [0, 0, 0, 0];

    public double MinHeightMeters { get; set; }

    public double MaxHeightMeters { get; set; }

    public string[] Warnings { get; set; } = [];
}

public sealed class SceneJobAcceptedResponse
{
    public string JobId { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string? SceneId { get; set; }

    public string StatusUrl { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SceneJobStatusResponse
{
    public string JobId { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string? SceneId { get; set; }

    public string StatusUrl { get; set; } = string.Empty;

    public string? TilesetUrl { get; set; }

    public string? I3sUrl { get; set; }

    public int? TileCount { get; set; }

    public int? FileCount { get; set; }

    public long? ByteSize { get; set; }

    public string[] Warnings { get; set; } = [];

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class SceneAssetLifecycleResponse
{
    public string SceneId { get; set; } = string.Empty;

    public Guid? DatasetId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string SourceKind { get; set; } = string.Empty;

    public string DatasetType { get; set; } = "hosted_tiles";

    public string LifecycleStatus { get; set; } = string.Empty;

    public string? JobStatus { get; set; }

    public string? LatestJobId { get; set; }

    public int? PublishedRevision { get; set; }

    public string? TilesetUrl { get; set; }

    public string? I3sUrl { get; set; }

    public long ByteSize { get; set; }

    public int FileCount { get; set; }

    public string[] Warnings { get; set; } = [];

    public bool IsPublic { get; set; }

    public bool RequiresAuth { get; set; }

    public string? Crs { get; set; }

    public SceneExtentDto? Extent { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }
}

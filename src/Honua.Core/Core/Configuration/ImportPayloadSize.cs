// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Configuration;

// Limits enforcement and the tile-package handler share this ceiling so the two cannot drift.
public static class ImportPayloadSize
{
    public const long MaxSceneArchiveBytes = 256L * 1024 * 1024;
    public const long MaxSceneModelBytes = 128L * 1024 * 1024;
    public const long SceneMultipartOverheadBytes = 1024 * 1024;

    public static long Resolve(string? path, LimitsOptions limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        path ??= string.Empty;

        if (path.Contains("/admin/import/preview", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Imports.MaxPreviewSize;
        }

        if (path.Contains("/admin/import/raster", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/formats", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Imports.MaxSyncImportSize;
        }

        if (path.Contains("/admin/import/tile-package", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Imports.MaxTilePackageSize;
        }

        if (path.Contains("/admin/import/upload", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Imports.MaxImportSize;
        }

        if (path.Contains("/admin/geocoding/reference-data/import", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Imports.MaxImportSize;
        }

        if (ResolveSceneUploadLimit(path) is { } sceneLimit)
        {
            return sceneLimit;
        }

        if (path.EndsWith("/addAttachment", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/updateAttachment", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Attachments.MaxAttachmentSize;
        }

        return limits.Edits.MaxPayloadSize;
    }

    private static long? ResolveSceneUploadLimit(string path)
    {
        var route = path.AsSpan().TrimEnd('/');
        if (route.EndsWith("/admin/scenes/ingest/archive", StringComparison.OrdinalIgnoreCase))
        {
            return MaxSceneArchiveBytes + SceneMultipartOverheadBytes;
        }

        if (route.EndsWith("/admin/scenes/ingest/model", StringComparison.OrdinalIgnoreCase))
        {
            return MaxSceneModelBytes + SceneMultipartOverheadBytes;
        }

        return null;
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Resolves the request-body ceiling for an admin route from <see cref="LimitsOptions"/>.
/// Limits enforcement and the tile-package handler both call this so the two cannot drift.
/// </summary>
public static class ImportPayloadSize
{
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

        if (path.EndsWith("/addAttachment", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/updateAttachment", StringComparison.OrdinalIgnoreCase))
        {
            return limits.Attachments.MaxAttachmentSize;
        }

        return limits.Edits.MaxPayloadSize;
    }
}

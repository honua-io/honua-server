// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Protocols.GeoServices.ImageServer;

// The tile key includes every input that changes the rendered bytes, including tenant and auth identity, so two different renders cannot collide.
internal static class ImageServerTileCacheKey
{
    internal static string Build(
        CloudStorageOptions? storageOptions,
        string metadataEtag,
        string publicationId,
        int layerId,
        string tileMatrixSetId,
        string styleId,
        string tenantAuthKey,
        IReadOnlyList<RasterInfo> selectedRasters,
        RasterMergeStrategy mergeStrategy,
        DateTimeOffset? timestamp,
        DateTimeOffset? timeStart,
        string mosaicRule,
        RasterFormat rasterFormat,
        int level,
        int row,
        int col,
        RasterTileWindow? window = null)
    {
        var rasterKey = string.Join(
            ',',
            selectedRasters.Select(static raster => raster.Id.ToString(CultureInfo.InvariantCulture)));

        // The behavior hash folds every render-affecting dimension into a stable digest. The
        // matrix set, style, and tenant/auth identity are included alongside the pre-existing
        // layer/raster/merge/time/format inputs so a change in any of them yields a distinct key.
        var behaviorHash = GeoServicesCloudTileCache.Hash(string.Join(
            '|',
            // Sampling directly from target centers changes edge pixels. Preserve old objects,
            // but never admit them under the corrected renderer's behavior key.
            rasterFormat == RasterFormat.PNG ? "target-centers-nearest-png-opacity-v2" : "target-centers-nearest-v1",
            metadataEtag,
            publicationId,
            layerId.ToString(CultureInfo.InvariantCulture),
            tileMatrixSetId,
            styleId,
            tenantAuthKey,
            rasterKey,
            mergeStrategy.ToString(),
            BuildTimeKey(timestamp, timeStart),
            mosaicRule,
            rasterFormat.ToString(),
            BuildWindowKey(window)));

        // Publication scope, matrix set, and style are also explicit path segments (not only
        // behavior-hashed) so lifecycle operations can target one service publication safely.
        return GeoServicesCloudTileCache.BuildObjectKey(
            storageOptions,
            "imageserver",
            "tiles",
            "v2",
            TileCachePublicationScope.Create(publicationId),
            layerId.ToString(CultureInfo.InvariantCulture),
            tileMatrixSetId,
            styleId,
            behaviorHash,
            level.ToString(CultureInfo.InvariantCulture),
            col.ToString(CultureInfo.InvariantCulture),
            $"{row.ToString(CultureInfo.InvariantCulture)}.{GetTileFileExtension(rasterFormat)}");
    }

    internal static string GetTileFileExtension(RasterFormat rasterFormat)
        => rasterFormat switch
        {
            RasterFormat.JPEG => "jpg",
            RasterFormat.TIFF or RasterFormat.COG => "tif",
            _ => "png"
        };

    // An instant keeps its pre-extent key (epoch ms) so existing cached tiles stay addressable; a
    // start,end extent adds its start bound so two windows sharing an end never collide.
    private static string BuildTimeKey(DateTimeOffset? timestamp, DateTimeOffset? timeStart)
    {
        var end = timestamp?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        return timeStart is { } start
            ? $"{start.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}~{end}"
            : end;
    }

    private static string BuildWindowKey(RasterTileWindow? window)
    {
        if (window is not { } value)
        {
            return string.Empty;
        }

        return string.Join(
            ',',
            value.MinX.ToString("R", CultureInfo.InvariantCulture),
            value.MinY.ToString("R", CultureInfo.InvariantCulture),
            value.MaxX.ToString("R", CultureInfo.InvariantCulture),
            value.MaxY.ToString("R", CultureInfo.InvariantCulture),
            value.Srid.ToString(CultureInfo.InvariantCulture),
            value.TileWidth.ToString(CultureInfo.InvariantCulture),
            value.TileHeight.ToString(CultureInfo.InvariantCulture));
    }
}

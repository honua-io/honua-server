// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer;

/// <summary>
/// Builds the durable cloud tile-cache object key for an ImageServer WMTS tile. The key is
/// partitioned by every dimension that changes the rendered bytes so two requests that differ in
/// any of them can never collide: the tile matrix set (gridset), style, output format, time,
/// tenant/auth identity, and the layer identity (layer id plus the participating raster set and
/// metadata graph revision).
/// </summary>
internal static class ImageServerTileCacheKey
{
    /// <summary>
    /// Builds the cloud tile-cache object key for a single ImageServer tile.
    /// </summary>
    internal static string Build(
        CloudStorageOptions? storageOptions,
        string metadataEtag,
        int layerId,
        string tileMatrixSetId,
        string styleId,
        string tenantAuthKey,
        IReadOnlyList<RasterInfo> selectedRasters,
        RasterMergeStrategy mergeStrategy,
        DateTimeOffset? timestamp,
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
            metadataEtag,
            layerId.ToString(CultureInfo.InvariantCulture),
            tileMatrixSetId,
            styleId,
            tenantAuthKey,
            rasterKey,
            mergeStrategy.ToString(),
            timestamp?.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            mosaicRule,
            rasterFormat.ToString(),
            BuildWindowKey(window)));

        // The matrix set and style are also explicit path segments (not only hashed) so cache
        // objects stay human-navigable and structurally isolated per gridset/style.
        return GeoServicesCloudTileCache.BuildObjectKey(
            storageOptions,
            "imageserver",
            "tiles",
            layerId.ToString(CultureInfo.InvariantCulture),
            tileMatrixSetId,
            styleId,
            behaviorHash,
            level.ToString(CultureInfo.InvariantCulture),
            col.ToString(CultureInfo.InvariantCulture),
            $"{row.ToString(CultureInfo.InvariantCulture)}.{GetTileFileExtension(rasterFormat)}");
    }

    /// <summary>
    /// Returns the file extension used for a cached tile of the given raster format.
    /// </summary>
    internal static string GetTileFileExtension(RasterFormat rasterFormat)
        => rasterFormat switch
        {
            RasterFormat.JPEG => "jpg",
            RasterFormat.TIFF or RasterFormat.COG => "tif",
            _ => "png"
        };

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

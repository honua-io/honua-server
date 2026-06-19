// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Helpers;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Tiles.Models;

namespace Honua.Protocols.Ogc.Api.Tiles;

internal static class OgcTilesUtilities
{
    private static readonly string[] _pngTileMediaTypes = ["image/png"];

    private static readonly string[] _vectorTileMediaTypes =
    [
        MediaTypes.Mvt,
        "application/x-protobuf",
        "application/octet-stream"
    ];

    /// <summary>
    /// Allowed query parameter sets for OGC Tiles endpoints.
    /// </summary>
    public static class AllowedQueryParameters
    {
        /// <summary>
        /// Allowed parameters for tile matrix set metadata endpoints.
        /// </summary>
        public static readonly FrozenSet<string> Metadata =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Allowed parameters for dataset tileset metadata endpoints.
        /// </summary>
        public static readonly FrozenSet<string> DatasetTilesetMetadata = new[]
            {
                "f",
                "collections"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Allowed parameters for OpenAPI endpoints.
        /// </summary>
        public static readonly FrozenSet<string> OpenApi =
            new[] { "f" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Allowed parameters for tiles endpoints.
        /// </summary>
        public static readonly FrozenSet<string> Tiles = new[]
            {
                "f",
                "datetime",
                "subset",
                "crs",
                "subset-crs"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Allowed parameters for dataset tiles endpoints.
        /// </summary>
        public static readonly FrozenSet<string> DatasetTiles = new[]
            {
                "f",
                "datetime",
                "subset",
                "crs",
                "subset-crs",
                "collections"
            }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public const string WebMercatorQuadId = "WebMercatorQuad";
    public const string WebMercatorQuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";
    public const string WebMercatorCrs = "http://www.opengis.net/def/crs/EPSG/0/3857";
    public const string WebMercatorScaleSet = "http://www.opengis.net/def/wkss/OGC/1.0/GoogleMapsCompatible";
    public const string WebMercatorTitle = "Web Mercator Quad";

    /// <summary>
    /// WorldCRS84Quad tile matrix set identifier.
    /// </summary>
    public const string WorldCrs84QuadId = "WorldCRS84Quad";

    /// <summary>
    /// WorldCRS84Quad tile matrix set URI.
    /// </summary>
    public const string WorldCrs84QuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WorldCRS84Quad";

    /// <summary>
    /// OGC CRS84 URI.
    /// </summary>
    public const string Crs84 = "http://www.opengis.net/def/crs/OGC/1.3/CRS84";

    /// <summary>
    /// WorldCRS84Quad well-known scale set URI.
    /// </summary>
    public const string WorldCrs84ScaleSet = "http://www.opengis.net/def/wkss/OGC/1.0/GoogleCRS84Quad";

    /// <summary>
    /// WorldCRS84Quad display title.
    /// </summary>
    public const string WorldCrs84Title = "World CRS84 Quad";

    private const double WebMercatorExtent = SpatialConstants.WebMercatorExtent;
    private const int DefaultTileSize = 256;
    private const double PixelSizeMeters = SpatialConstants.PixelSizeMeters;

    /// <summary>
    /// Cell size in degrees per pixel at the equator (standardised OGC value).
    /// </summary>
    private const double DegreesPerPixel = SpatialConstants.DegreesPerPixel;

    /// <summary>
    /// Returns true when the tile matrix set identifier is WorldCRS84Quad.
    /// </summary>
    public static bool IsWorldCrs84Quad(string tileMatrixSetId)
        => string.Equals(tileMatrixSetId, WorldCrs84QuadId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the well-known scale set URI advertised for a registered tile matrix set. The two
    /// built-ins keep their canonical OGC well-known scale sets; operator-defined custom gridsets
    /// advertise none.
    /// </summary>
    public static string? GetWellKnownScaleSet(TileMatrixSetEntry entry)
    {
        if (!entry.IsBuiltIn)
        {
            return null;
        }

        return string.Equals(entry.Id, WorldCrs84QuadId, StringComparison.OrdinalIgnoreCase)
            ? WorldCrs84ScaleSet
            : WebMercatorScaleSet;
    }

    /// <summary>
    /// Builds a tile matrix set summary item from a registry entry.
    /// </summary>
    public static TileMatrixSetItem BuildTileMatrixSetItem(TileMatrixSetEntry entry, string baseUrl)
    {
        var selfHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{entry.Id}";
        return new TileMatrixSetItem
        {
            Id = entry.Id,
            Title = entry.Title,
            Uri = entry.Uri,
            Crs = entry.Crs,
            Links = ImmutableArray.Create(Link.Create(
                href: selfHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"))
        };
    }

    /// <summary>
    /// Builds the full tile matrix set definition from a registry entry and its grid geometry.
    /// For the two built-ins this reproduces the exact byte-identical output of the legacy
    /// <c>BuildWebMercatorQuadDefinition</c>/<c>BuildWorldCrs84QuadDefinition</c> paths because the
    /// registry seeds them from the same constants and formulas.
    /// </summary>
    public static TileMatrixSetDefinition BuildTileMatrixSetDefinition(
        TileMatrixSetEntry entry,
        GridGeometry geometry,
        int minLevel = 0)
    {
        var tileMatrices = ImmutableArray.CreateBuilder<TileMatrix>(geometry.Levels.Length);
        foreach (var level in geometry.Levels)
        {
            if (level.Level < minLevel)
            {
                continue;
            }

            tileMatrices.Add(new TileMatrix
            {
                Id = level.Level.ToString(CultureInfo.InvariantCulture),
                ScaleDenominator = level.ScaleDenominator,
                CellSize = level.CellSize,
                PointOfOrigin = ImmutableArray.Create(geometry.TopLeftX, geometry.TopLeftY),
                TileWidth = geometry.TileWidth,
                TileHeight = geometry.TileHeight,
                MatrixWidth = checked((int)level.MatrixWidth),
                MatrixHeight = checked((int)level.MatrixHeight),
                CornerOfOrigin = "topLeft"
            });
        }

        return new TileMatrixSetDefinition
        {
            Id = entry.Id,
            Title = entry.Title,
            Uri = entry.Uri,
            Crs = entry.Crs,
            WellKnownScaleSet = GetWellKnownScaleSet(entry),
            TileMatrices = tileMatrices.ToImmutable()
        };
    }

    /// <summary>
    /// Builds tile matrix set limits (full coverage) from a grid geometry. Each level exposes the
    /// full matrix-width × matrix-height extent.
    /// </summary>
    public static ImmutableArray<TileMatrixSetLimit> BuildTileMatrixSetLimits(GridGeometry geometry, int minLevel = 0)
    {
        var matrixLimits = ImmutableArray.CreateBuilder<TileMatrixSetLimit>(geometry.Levels.Length);
        foreach (var level in geometry.Levels)
        {
            if (level.Level < minLevel)
            {
                continue;
            }

            matrixLimits.Add(new TileMatrixSetLimit
            {
                TileMatrix = level.Level.ToString(CultureInfo.InvariantCulture),
                MinTileRow = 0,
                MaxTileRow = checked((int)(level.MatrixHeight - 1)),
                MinTileCol = 0,
                MaxTileCol = checked((int)(level.MatrixWidth - 1))
            });
        }

        return matrixLimits.ToImmutable();
    }

    public static TileMatrixSetItem BuildWebMercatorQuadItem(string baseUrl)
    {
        var selfHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{WebMercatorQuadId}";
        return new TileMatrixSetItem
        {
            Id = WebMercatorQuadId,
            Title = WebMercatorTitle,
            Uri = WebMercatorQuadUri,
            Crs = WebMercatorCrs,
            Links = ImmutableArray.Create(Link.Create(
                href: selfHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"))
        };
    }

    public static TileMatrixSetDefinition BuildWebMercatorQuadDefinition(TileLimits limits)
    {
        var minZoom = Math.Max(0, limits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, limits.MaxTileZoom);
        var tileMatrices = new List<TileMatrix>();

        for (var z = minZoom; z <= maxZoom; z++)
        {
            var matrixSize = 1 << z;
            var cellSize = (2.0 * WebMercatorExtent) / ((double)DefaultTileSize * matrixSize);
            var scaleDenominator = cellSize / PixelSizeMeters;

            tileMatrices.Add(new TileMatrix
            {
                Id = z.ToString(CultureInfo.InvariantCulture),
                ScaleDenominator = scaleDenominator,
                CellSize = cellSize,
                PointOfOrigin = ImmutableArray.Create(-WebMercatorExtent, WebMercatorExtent),
                TileWidth = DefaultTileSize,
                TileHeight = DefaultTileSize,
                MatrixWidth = matrixSize,
                MatrixHeight = matrixSize,
                CornerOfOrigin = "topLeft"
            });
        }

        return new TileMatrixSetDefinition
        {
            Id = WebMercatorQuadId,
            Title = WebMercatorTitle,
            Uri = WebMercatorQuadUri,
            Crs = WebMercatorCrs,
            WellKnownScaleSet = WebMercatorScaleSet,
            TileMatrices = tileMatrices.ToImmutableArray()
        };
    }

    /// <summary>
    /// Builds a <see cref="TileMatrixSetItem"/> for WorldCRS84Quad.
    /// </summary>
    public static TileMatrixSetItem BuildWorldCrs84QuadItem(string baseUrl)
    {
        var selfHref = $"{baseUrl}/ogc/tiles/tileMatrixSets/{WorldCrs84QuadId}";
        return new TileMatrixSetItem
        {
            Id = WorldCrs84QuadId,
            Title = WorldCrs84Title,
            Uri = WorldCrs84QuadUri,
            Crs = Crs84,
            Links = ImmutableArray.Create(Link.Create(
                href: selfHref,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "Tile matrix set definition"))
        };
    }

    /// <summary>
    /// Builds the full WorldCRS84Quad tile matrix set definition.
    /// </summary>
    public static TileMatrixSetDefinition BuildWorldCrs84QuadDefinition(TileLimits limits)
    {
        var minZoom = Math.Max(0, limits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, limits.MaxTileZoom);
        var tileMatrices = new List<TileMatrix>();

        for (var z = minZoom; z <= maxZoom; z++)
        {
            // WorldCRS84Quad: at zoom 0, 2 cols x 1 row covering the full globe
            var numRows = 1 << z;
            var numCols = numRows * 2;
            var cellSize = 180.0 / ((double)DefaultTileSize * numRows);
            var scaleDenominator = cellSize / DegreesPerPixel;

            tileMatrices.Add(new TileMatrix
            {
                Id = z.ToString(CultureInfo.InvariantCulture),
                ScaleDenominator = scaleDenominator,
                CellSize = cellSize,
                PointOfOrigin = ImmutableArray.Create(-180.0, 90.0),
                TileWidth = DefaultTileSize,
                TileHeight = DefaultTileSize,
                MatrixWidth = numCols,
                MatrixHeight = numRows,
                CornerOfOrigin = "topLeft"
            });
        }

        return new TileMatrixSetDefinition
        {
            Id = WorldCrs84QuadId,
            Title = WorldCrs84Title,
            Uri = WorldCrs84QuadUri,
            Crs = Crs84,
            WellKnownScaleSet = WorldCrs84ScaleSet,
            TileMatrices = tileMatrices.ToImmutableArray()
        };
    }

    /// <summary>
    /// Builds tile matrix set limits for WebMercatorQuad (square tiles, 1×1 at zoom 0).
    /// </summary>
    public static ImmutableArray<TileMatrixSetLimit> BuildWebMercatorQuadLimits(TileLimits limits)
    {
        var minZoom = Math.Max(0, limits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, limits.MaxTileZoom);
        var matrixLimits = new List<TileMatrixSetLimit>();

        for (var zoom = minZoom; zoom <= maxZoom; zoom++)
        {
            var matrixSize = 1 << zoom;
            var maxIndex = matrixSize - 1;

            matrixLimits.Add(new TileMatrixSetLimit
            {
                TileMatrix = zoom.ToString(CultureInfo.InvariantCulture),
                MinTileRow = 0,
                MaxTileRow = maxIndex,
                MinTileCol = 0,
                MaxTileCol = maxIndex
            });
        }

        return matrixLimits.ToImmutableArray();
    }

    /// <summary>
    /// Builds tile matrix set limits for WorldCRS84Quad (2 cols x 1 row at zoom 0).
    /// </summary>
    public static ImmutableArray<TileMatrixSetLimit> BuildWorldCrs84QuadLimits(TileLimits limits)
    {
        var minZoom = Math.Max(0, limits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, limits.MaxTileZoom);
        var matrixLimits = new List<TileMatrixSetLimit>();

        for (var zoom = minZoom; zoom <= maxZoom; zoom++)
        {
            var numCols = (int)(2 * Math.Pow(2, zoom));
            var numRows = (int)Math.Pow(2, zoom);

            matrixLimits.Add(new TileMatrixSetLimit
            {
                TileMatrix = zoom.ToString(CultureInfo.InvariantCulture),
                MinTileRow = 0,
                MaxTileRow = numRows - 1,
                MinTileCol = 0,
                MaxTileCol = numCols - 1
            });
        }

        return matrixLimits.ToImmutableArray();
    }

    /// <summary>
    /// Checks whether the request accepts vector tile responses.
    /// </summary>
    public static bool AcceptsVectorTiles(HttpRequest request)
    {
        var accept = request.Headers.Accept;
        if (Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(accept))
        {
            return true;
        }

        var vectorQuality = ContentNegotiationHelpers.GetBestQuality(_vectorTileMediaTypes, accept);
        return vectorQuality > 0;
    }

    /// <summary>
    /// Checks whether the request asks for a PNG raster tile.
    /// </summary>
    public static bool AcceptsPngTiles(HttpRequest request)
    {
        var accept = request.Headers.Accept;
        if (Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(accept))
        {
            return false;
        }

        var pngQuality = ContentNegotiationHelpers.GetBestQuality(_pngTileMediaTypes, accept);
        return pngQuality > 0;
    }

    /// <summary>
    /// Determines whether the requested tile format is PNG (raster).
    /// Checks the <c>f</c> query parameter and <c>Accept</c> header.
    /// </summary>
    public static bool IsRasterTileFormat(string? format, HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(format) &&
            string.Equals(format, "png", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Explicit MVT request or no format specified - not raster
        if (!string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        var accept = request.Headers.Accept;
        if (Microsoft.Extensions.Primitives.StringValues.IsNullOrEmpty(accept))
        {
            return false;
        }

        var pngQuality = ContentNegotiationHelpers.GetBestQuality(_pngTileMediaTypes, accept);
        var vectorQuality = ContentNegotiationHelpers.GetBestQuality(_vectorTileMediaTypes, accept);
        return pngQuality > vectorQuality;
    }

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
        => TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
}

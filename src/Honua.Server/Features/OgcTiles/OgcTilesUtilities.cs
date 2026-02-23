// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Configuration;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcTiles.Models;

namespace Honua.Server.Features.OgcTiles;

internal static class OgcTilesUtilities
{
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
    /// EPSG:4326 CRS URI.
    /// </summary>
    public const string Crs84 = "http://www.opengis.net/def/crs/EPSG/0/4326";

    /// <summary>
    /// WorldCRS84Quad well-known scale set URI.
    /// </summary>
    public const string WorldCrs84ScaleSet = "http://www.opengis.net/def/wkss/OGC/1.0/GoogleCRS84Quad";

    /// <summary>
    /// WorldCRS84Quad display title.
    /// </summary>
    public const string WorldCrs84Title = "World CRS84 Quad";

    private const double WebMercatorExtent = 20037508.342789244;
    private const int DefaultTileSize = 256;
    private const double PixelSizeMeters = 0.00028;

    /// <summary>
    /// Cell size in degrees per pixel at the equator (standardised OGC value).
    /// </summary>
    private const double DegreesPerPixel = 0.00028 * 180.0 / 20037508.342789244;

    /// <summary>
    /// Determines whether the given tile matrix set identifier is supported.
    /// </summary>
    public static bool IsSupportedTileMatrixSet(string tileMatrixSetId)
        => string.Equals(tileMatrixSetId, WebMercatorQuadId, StringComparison.OrdinalIgnoreCase)
           || string.Equals(tileMatrixSetId, WorldCrs84QuadId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the tile matrix set identifier is WorldCRS84Quad.
    /// </summary>
    public static bool IsWorldCrs84Quad(string tileMatrixSetId)
        => string.Equals(tileMatrixSetId, WorldCrs84QuadId, StringComparison.OrdinalIgnoreCase);

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
            var cellSize = (2.0 * WebMercatorExtent) / (DefaultTileSize * matrixSize);
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
            var numCols = (int)(2 * Math.Pow(2, z));
            var numRows = (int)Math.Pow(2, z);
            var cellSize = 180.0 / (DefaultTileSize * numRows);
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
        var acceptHeader = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        return acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains(MediaTypes.Mvt, StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase) ||
               acceptHeader.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether the request asks for a PNG raster tile.
    /// </summary>
    public static bool AcceptsPngTiles(HttpRequest request)
    {
        var acceptHeader = request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return false;
        }

        return acceptHeader.Contains("image/png", StringComparison.OrdinalIgnoreCase);
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

        // Fall back to Accept header: prefer PNG when explicitly requested
        return AcceptsPngTiles(request) && !AcceptsVectorTiles(request);
    }

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
        => TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
}

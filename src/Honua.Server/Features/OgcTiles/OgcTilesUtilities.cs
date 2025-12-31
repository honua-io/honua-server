// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcTiles.Models;

namespace Honua.Server.Features.OgcTiles;

internal static class OgcTilesUtilities
{
    public static class AllowedQueryParameters
    {
        public static readonly ISet<string> Metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> DatasetTilesetMetadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "collections"
        };

        public static readonly ISet<string> OpenApi = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> Tiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "datetime",
            "subset",
            "crs",
            "subset-crs"
        };

        public static readonly ISet<string> DatasetTiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "datetime",
            "subset",
            "crs",
            "subset-crs",
            "collections"
        };
    }

    public const string WebMercatorQuadId = "WebMercatorQuad";
    public const string WebMercatorQuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";
    public const string WebMercatorCrs = "http://www.opengis.net/def/crs/EPSG/0/3857";
    public const string WebMercatorScaleSet = "http://www.opengis.net/def/wkss/OGC/1.0/GoogleMapsCompatible";
    public const string WebMercatorTitle = "Web Mercator Quad";

    private const double WebMercatorExtent = 20037508.342789244;
    private const int DefaultTileSize = 256;
    private const double PixelSizeMeters = 0.00028;

    public static bool IsSupportedTileMatrixSet(string tileMatrixSetId)
        => string.Equals(tileMatrixSetId, WebMercatorQuadId, StringComparison.OrdinalIgnoreCase);

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

    public static TileMatrixSetDefinition BuildWebMercatorQuadDefinition(TileOptions options)
    {
        var minZoom = Math.Max(0, options.MinZoom);
        var maxZoom = Math.Max(minZoom, options.MaxZoom);
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

    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue("LimitsTimeoutToken", out var tokenObj) && tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }
}

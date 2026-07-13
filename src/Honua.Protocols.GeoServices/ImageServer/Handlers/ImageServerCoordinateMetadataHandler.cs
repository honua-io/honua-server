// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using SpatialConstants = Honua.Core.Features.Shared.Models.SpatialConstants;
using SpatialReferenceExtensions = Honua.Core.Features.Shared.Models.SpatialReferenceExtensions;
using WebMercatorMath = Honua.Core.Features.Shared.Models.WebMercatorMath;
using IGeographicSridClassifier = Honua.Core.Features.Shared.Models.IGeographicSridClassifier;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for ImageServer coordinate and boundary metadata operations.
/// </summary>
internal sealed class ImageServerCoordinateMetadataHandler
{
    private const double WebMercatorEarthRadius = 6378137d;

    private readonly IRasterStore _rasterStore;
    private readonly IImageServerCatalogReader _catalogReader;
    private readonly ILogger<ImageServerCoordinateMetadataHandler> _logger;
    private readonly ICoordinateTransformService? _transformService;
    private readonly IGeographicSridClassifier? _geographicSridClassifier;

    public ImageServerCoordinateMetadataHandler(
        IRasterStore rasterStore,
        IImageServerCatalogReader catalogReader,
        ILogger<ImageServerCoordinateMetadataHandler> logger,
        ICoordinateTransformService? transformService = null,
        IGeographicSridClassifier? geographicSridClassifier = null)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _catalogReader = catalogReader ?? throw new ArgumentNullException(nameof(catalogReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transformService = transformService;
        _geographicSridClassifier = geographicSridClassifier;
    }

    /// <summary>
    /// Returns conservative ImageServer cache metadata for dynamic raster layers.
    /// </summary>
    public async Task<IResult> ComputeCacheInfoAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "compute-cache-info",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "compute-cache-info");

        try
        {
            if (!TryParseOutSrid(values, out var outSrid, out var parseError))
            {
                ImageServerLog.InvalidCatalogQueryParameters(_logger, layerId, parseError ?? "Invalid outSR.");
                return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid outSR.");
            }

            var extent = await ReadAggregateExtentAsync(layerId, outSrid, cancellationToken).ConfigureAwait(false);
            if (extent is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var response = new ComputeCacheInfoResponse
            {
                CacheInfo = new ImageServerComputedCacheInfo
                {
                    Extent = ToImageServerExtent(extent.Value),
                    // Honua ImageServer currently serves dynamic imagery. Do not
                    // synthesize tileInfo because that makes clients treat the layer
                    // as a generated raster tile cache.
                    TileInfo = null,
                    CacheType = null,
                },
            };

            scope.SetSuccess(1);
            return Results.Json(response, ImageServerJsonContext.Default.ComputeCacheInfoResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.ServiceInfoFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while computing image service cache metadata.");
        }
    }

    /// <summary>
    /// Computes image pixel column/row coordinates for map-space point geometries.
    /// </summary>
    public async Task<IResult> ComputePixelLocationAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "compute-pixel-location",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "compute-pixel-location");

        try
        {
            if (!TryReadInputPoints(values, out var inputPoints, out var pointError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, pointError ?? "Invalid geometries.");
                return StandardErrorHelpers.CreateBadRequest(context, pointError ?? "Invalid geometries.");
            }

            if (!TryResolveRasterId(values, out var rasterId, out var rasterIdError))
            {
                ImageServerLog.InvalidIdentifyParameters(_logger, layerId, rasterIdError ?? "Invalid rasterId.");
                return StandardErrorHelpers.CreateBadRequest(context, rasterIdError ?? "Invalid rasterId.");
            }

            var raster = rasterId.HasValue
                ? await _rasterStore.GetRasterInfoAsync(layerId, rasterId.Value, cancellationToken).ConfigureAwait(false)
                : await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (raster is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Raster not found.");
            }

            var requestSrid = ReadRequestSpatialReference(values);
            var pixelPoints = new List<PixelLocationPoint>(inputPoints.Count);
            foreach (var point in inputPoints)
            {
                var mapPoint = await TransformToRasterSridAsync(
                    point,
                    requestSrid,
                    raster.Value.Srid,
                    cancellationToken).ConfigureAwait(false);
                if (mapPoint is null)
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Input geometries could not be transformed into the raster spatial reference.");
                }

                if (!TryMapToPixel(raster.Value, mapPoint.Value.X, mapPoint.Value.Y, out var column, out var row))
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Raster georeferencing metadata is not sufficient to compute pixel locations.");
                }

                pixelPoints.Add(new PixelLocationPoint { X = column, Y = row });
            }

            ImageServerLog.IdentifyCompleted(_logger, layerId, pixelPoints.Count > 0, pixelPoints.Count);
            scope.SetSuccess(pixelPoints.Count);

            var response = new ComputePixelLocationResponse { Geometries = pixelPoints.ToArray() };
            return Results.Json(response, ImageServerJsonContext.Default.ComputePixelLocationResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.IdentifyFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while computing pixel locations.");
        }
    }

    /// <summary>
    /// Returns the aggregate raster footprint boundary for an ImageServer layer.
    /// </summary>
    public async Task<IResult> QueryBoundaryAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "query-boundary",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "query-boundary");

        try
        {
            if (!TryParseOutSrid(values, out var outSrid, out var parseError))
            {
                ImageServerLog.InvalidCatalogQueryParameters(_logger, layerId, parseError ?? "Invalid outSR.");
                return StandardErrorHelpers.CreateBadRequest(context, parseError ?? "Invalid outSR.");
            }

            var extent = await ReadAggregateExtentAsync(layerId, outSrid, cancellationToken).ConfigureAwait(false);
            if (extent is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var shape = BuildBoundaryShape(extent.Value);
            var extentIsGeographic = await ResolveExtentIsGeographicForMeasurementAsync(
                extent.Value.Srid, cancellationToken).ConfigureAwait(false);
            var response = new QueryBoundaryResponse
            {
                Shape = shape,
                Area = CalculateApproximateArea(extent.Value, extentIsGeographic),
            };

            ImageServerLog.CatalogQueryCompleted(_logger, layerId, 1, 1);
            scope.SetSuccess(1);
            return Results.Json(response, ImageServerJsonContext.Default.QueryBoundaryResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.CatalogQueryFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while querying the image service boundary.");
        }
    }

    private async Task<RasterExtent?> ReadAggregateExtentAsync(
        int layerId,
        int? outSrid,
        CancellationToken cancellationToken)
    {
        var page = await _catalogReader.ReadAsync(
            layerId,
            new ImageServerCatalogQuery
            {
                OutputSrid = outSrid,
                ReturnExtentOnly = true,
                Offset = 0,
                Limit = int.MaxValue,
            },
            cancellationToken).ConfigureAwait(false);
        return page.AggregateExtent;
    }

    private async ValueTask<MapPoint?> TransformToRasterSridAsync(
        MapPoint point,
        int? requestSrid,
        int? rasterSrid,
        CancellationToken cancellationToken)
    {
        var sourceSrid = point.Srid ?? requestSrid;
        if (!sourceSrid.HasValue || !rasterSrid.HasValue || sourceSrid.Value == rasterSrid.Value)
        {
            return point;
        }

        if (_transformService is null)
        {
            return null;
        }

        var transformed = await _transformService.TransformPointAsync(
            point.X,
            point.Y,
            sourceSrid.Value,
            rasterSrid.Value,
            cancellationToken).ConfigureAwait(false);

        return transformed.HasValue
            ? new MapPoint(transformed.Value.X, transformed.Value.Y, rasterSrid)
            : null;
    }

    private static bool TryReadInputPoints(
        IReadOnlyDictionary<string, StringValues> values,
        out IReadOnlyList<MapPoint> points,
        out string? error)
    {
        points = Array.Empty<MapPoint>();
        error = null;

        var geometries = GetString(values, "geometries");
        if (!string.IsNullOrWhiteSpace(geometries))
        {
            return TryReadGeometriesPayload(geometries, out points, out error);
        }

        var geometry = GetString(values, "geometry");
        if (!string.IsNullOrWhiteSpace(geometry))
        {
            var normalized = NormalizeGeometryInput(geometry);
            if (!ImageServerGeometryHelpers.TryGetSamplePoints(normalized, out var samplePoints, out error))
            {
                return false;
            }

            points = samplePoints.Select(p => new MapPoint(p.X, p.Y, p.Srid)).ToArray();
            return points.Count > 0;
        }

        error = "geometries is required.";
        return false;
    }

    private static bool TryReadGeometriesPayload(
        string raw,
        out IReadOnlyList<MapPoint> points,
        out string? error)
    {
        points = Array.Empty<MapPoint>();
        error = null;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var fallbackSrid = TryReadWkid(root);

            JsonElement geometriesElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("geometries", out var nestedGeometries))
            {
                geometriesElement = nestedGeometries;
                fallbackSrid = TryReadWkid(root) ?? fallbackSrid;
            }
            else
            {
                geometriesElement = root;
            }

            var parsed = new List<MapPoint>();
            if (geometriesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var geometry in geometriesElement.EnumerateArray())
                {
                    if (TryReadPointGeometry(geometry, fallbackSrid, out var point))
                    {
                        parsed.Add(point);
                    }
                }
            }
            else if (TryReadPointGeometry(geometriesElement, fallbackSrid, out var point))
            {
                parsed.Add(point);
            }

            if (parsed.Count == 0)
            {
                error = "geometries must contain point geometries.";
                return false;
            }

            points = parsed;
            return true;
        }
        catch (JsonException)
        {
            error = "geometries must be valid JSON.";
            return false;
        }
    }

    private static bool TryReadPointGeometry(JsonElement element, int? fallbackSrid, out MapPoint point)
    {
        point = default;
        if (element.ValueKind == JsonValueKind.Object &&
            TryReadNumber(element, "x", out var x) &&
            TryReadNumber(element, "y", out var y))
        {
            point = new MapPoint(x, y, TryReadWkid(element) ?? fallbackSrid);
            return true;
        }

        if (element.ValueKind == JsonValueKind.Array &&
            element.GetArrayLength() >= 2 &&
            element[0].ValueKind == JsonValueKind.Number &&
            element[1].ValueKind == JsonValueKind.Number)
        {
            point = new MapPoint(element[0].GetDouble(), element[1].GetDouble(), fallbackSrid);
            return true;
        }

        return false;
    }

    private static int? ReadRequestSpatialReference(IReadOnlyDictionary<string, StringValues> values)
        => ImageServerGeometryHelpers.TryParseSrid(GetString(values, "spatialReference"))
           ?? ImageServerGeometryHelpers.TryParseSrid(GetString(values, "sr"))
           ?? ImageServerGeometryHelpers.TryParseSrid(GetString(values, "inSR"));

    private static bool TryResolveRasterId(
        IReadOnlyDictionary<string, StringValues> values,
        out long? rasterId,
        out string? error)
    {
        rasterId = null;
        error = null;

        var raw = GetString(values, "rasterId");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            rasterId = parsed;
            return true;
        }

        error = "rasterId must be a non-negative integer.";
        return false;
    }

    private static bool TryParseOutSrid(
        IReadOnlyDictionary<string, StringValues> values,
        out int? outSrid,
        out string? error)
    {
        outSrid = null;
        error = null;

        var raw = GetString(values, "outSR") ?? GetString(values, "outSr");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        outSrid = ImageServerGeometryHelpers.TryParseSrid(raw);
        if (outSrid.HasValue)
        {
            return true;
        }

        error = $"Invalid outSR '{raw}'.";
        return false;
    }

    private static bool TryMapToPixel(
        RasterInfo raster,
        double x,
        double y,
        out double column,
        out double row)
    {
        column = 0;
        row = 0;

        if (raster.GeoTransform is { Length: >= 6 } transform)
        {
            var dx = x - transform[0];
            var dy = y - transform[3];
            var determinant = transform[1] * transform[5] - transform[2] * transform[4];
            if (Math.Abs(determinant) > 1e-12)
            {
                column = (dx * transform[5] - transform[2] * dy) / determinant;
                row = (transform[1] * dy - dx * transform[4]) / determinant;
                return true;
            }
        }

        if (raster.Extent is { } extent &&
            raster.Width > 0 &&
            raster.Height > 0 &&
            Math.Abs(extent.XMax - extent.XMin) > 1e-12 &&
            Math.Abs(extent.YMax - extent.YMin) > 1e-12)
        {
            column = (x - extent.XMin) / (extent.XMax - extent.XMin) * raster.Width;
            row = (extent.YMax - y) / (extent.YMax - extent.YMin) * raster.Height;
            return true;
        }

        return false;
    }

    private static CatalogQueryGeometry BuildBoundaryShape(RasterExtent extent)
    {
        var srid = extent.Srid ?? 4326;
        return new CatalogQueryGeometry
        {
            Rings =
            [
                [
                    [extent.XMin, extent.YMin],
                    [extent.XMin, extent.YMax],
                    [extent.XMax, extent.YMax],
                    [extent.XMax, extent.YMin],
                    [extent.XMin, extent.YMin],
                ],
            ],
            SpatialReference = new SpatialReference { Wkid = srid, LatestWkid = srid },
        };
    }

    private static ImageServerExtent ToImageServerExtent(RasterExtent extent)
    {
        var srid = extent.Srid ?? 4326;
        return new ImageServerExtent
        {
            XMin = extent.XMin,
            YMin = extent.YMin,
            XMax = extent.XMax,
            YMax = extent.YMax,
            SpatialReference = new SpatialReference { Wkid = srid, LatestWkid = srid },
        };
    }

    /// <summary>
    /// Resolves whether the extent's SRID measures as geographic (lon/lat degrees) for the offline
    /// area computation, preferring the registry-backed <see cref="IGeographicSridClassifier"/>
    /// (#2794) and falling back to the static
    /// <see cref="Honua.Core.Features.Shared.Models.GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(int)"/>
    /// heuristic when no classifier is injected (e.g. read-only providers without a CRS registry).
    /// </summary>
    private async ValueTask<bool> ResolveExtentIsGeographicForMeasurementAsync(
        int? srid,
        CancellationToken cancellationToken)
    {
        if (srid is not int value)
        {
            return false;
        }

        if (_geographicSridClassifier is not null)
        {
            return await _geographicSridClassifier
                .IsGeographicForMeasurementAsync(value, cancellationToken)
                .ConfigureAwait(false);
        }

        return Honua.Core.Features.Shared.Models.GeographicSridClassifier
            .IsGeographicOrUnlistedGeographicRangeSrid(value);
    }

    private static double CalculateApproximateArea(RasterExtent extent, bool isGeographicForMeasurement)
    {
        // Web Mercator (EPSG:3857) is conformal: an axis-aligned extent inverse-projects to an
        // axis-aligned lon/lat quadrangle, so inverse-project the corners and use the same
        // spherical-cap area formula as geographic extents (#2734).
        if (extent.Srid is int srid && SpatialReferenceExtensions.NormalizeWebMercatorSrid(srid) == 3857)
        {
            var (minLon, minLat) = WebMercatorMath.WebMercatorToLonLat(extent.XMin, extent.YMin);
            var (maxLon, maxLat) = WebMercatorMath.WebMercatorToLonLat(extent.XMax, extent.YMax);
            return SphericalQuadrangleArea(minLon, minLat, maxLon, maxLat);
        }

        // Geographic extents (WGS 84, NAD83, the other confirmed geographic SRIDs, and any
        // unlisted EPSG 4000–4999 geographic-block code such as EPSG:4301/4314/4322) are already
        // lon/lat degrees. The geographic determination is resolved by the caller through the
        // registry-backed IGeographicSridClassifier (#2794), which classifies arbitrary codes from
        // live spatial_ref_sys WKT and falls back to the static broad-list-plus-range heuristic
        // (#2732) so these codes are not mis-measured as degrees-squared-as-metres in the planar
        // branch below.
        if (isGeographicForMeasurement)
        {
            return SphericalQuadrangleArea(extent.XMin, extent.YMin, extent.XMax, extent.YMax);
        }

        // Other projected SRIDs: coordinates are assumed to be meters. Planar area is only
        // correct when the map units are truly meters; documented fallback rather than a claim
        // of ground area for degree/foot units.
        return Math.Abs((extent.XMax - extent.XMin) * (extent.YMax - extent.YMin));
    }

    private static double SphericalQuadrangleArea(double minLon, double minLat, double maxLon, double maxLat)
    {
        var lowLat = Math.Clamp(Math.Min(minLat, maxLat), -90d, 90d) * Math.PI / 180d;
        var highLat = Math.Clamp(Math.Max(minLat, maxLat), -90d, 90d) * Math.PI / 180d;
        var lonSpan = Math.Abs(maxLon - minLon) * Math.PI / 180d;
        return WebMercatorEarthRadius * WebMercatorEarthRadius * lonSpan *
               Math.Abs(Math.Sin(highLat) - Math.Sin(lowLat));
    }

    private static string NormalizeGeometryInput(string geometry)
    {
        var trimmed = geometry.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return $"{{\"x\":{parts[0]},\"y\":{parts[1]}}}";
        }

        if (parts.Length == 4 &&
            parts.All(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            return $"{{\"xmin\":{parts[0]},\"ymin\":{parts[1]},\"xmax\":{parts[2]},\"ymax\":{parts[3]}}}";
        }

        return trimmed;
    }

    private static bool TryReadNumber(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var propertyValue) &&
               propertyValue.ValueKind == JsonValueKind.Number &&
               propertyValue.TryGetDouble(out value);
    }

    private static int? TryReadWkid(JsonElement root)
    {
        if (!root.TryGetProperty("spatialReference", out var spatialReference) ||
            spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (spatialReference.TryGetProperty("latestWkid", out var latestWkid) &&
            latestWkid.ValueKind == JsonValueKind.Number &&
            latestWkid.TryGetInt32(out var latestValue))
        {
            return latestValue;
        }

        if (spatialReference.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number &&
            wkid.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;

    private readonly record struct MapPoint(double X, double Y, int? Srid);
}

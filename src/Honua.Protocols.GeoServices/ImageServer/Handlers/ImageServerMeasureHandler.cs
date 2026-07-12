// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

// Aliased to avoid clashing with the local ImageServer.Models.SpatialReference response type.
using CoreSpatialReference = Honua.Core.Features.Shared.Models.SpatialReference;
using SpatialReferenceExtensions = Honua.Core.Features.Shared.Models.SpatialReferenceExtensions;
using WebMercatorMath = Honua.Core.Features.Shared.Models.WebMercatorMath;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Basic ImageServer mensuration.
/// </summary>
internal sealed class ImageServerMeasureHandler
{
    // WGS84 equatorial radius. This is deliberately the same sphere Web Mercator (EPSG:3857) is
    // defined on, so unprojecting 3857 -> lon/lat and then measuring keeps round-trips exact.
    // Using the mean/authalic radius instead would introduce a ~0.11% bias and diverge from the
    // Web-Mercator sphere; that refinement is tracked separately as a minor item on #2734.
    private const double EarthRadiusMeters = 6378137d;

    private enum MeasureCrsKind
    {
        Geographic,
        WebMercator,
        ProjectedLinear
    }

    private readonly record struct MeasurePoint(double X, double Y, double? Z, int? Srid);

    private readonly record struct MeasureGeometry(
        MeasurePoint[] Points,
        string GeometryType,
        int? Srid);

    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerMeasureHandler> _logger;
    private readonly IElevationService? _elevationService;

    public ImageServerMeasureHandler(
        IRasterStore rasterStore,
        ILogger<ImageServerMeasureHandler> logger,
        IElevationService? elevationService = null)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _elevationService = elevationService;
    }

    /// <summary>
    /// Performs Basic ImageServer measure operations in map space.
    /// </summary>
    public async Task<IResult> MeasureAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "measure",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "measure")
            .WithTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (primary is not { } raster || raster.Id <= 0)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!TryParseRequest(values, raster.Srid, out var operation, out var fromGeometry, out var toGeometry, out var error))
            {
                ImageServerLog.InvalidMeasureParameters(_logger, layerId, error ?? "Invalid measure parameter.");
                return StandardErrorHelpers.CreateBadRequest(context, error ?? "Invalid measure parameter.");
            }

            var normalizedOperation = operation.ToLowerInvariant();

            // DEM-backed height mensuration (#1879): heightFromBaseAndTop differences the ground
            // elevation at the base point and the top point against the raster's associated DEM
            // (raster_sensor_metadata.dem_source). Shadow-based height and pure-3D operations still
            // require sensor exterior orientation / shadow geometry that is not modeled, so they
            // keep the honest 501. Height ops with no DEM metadata also return 501.
            if (normalizedOperation == "esrimensurationheightfrombaseandtop" && toGeometry is not null)
            {
                return await MeasureDemHeightAsync(
                    context, layerId, raster, fromGeometry.Points[0], toGeometry.Value.Points[0],
                    GetString(values, "linearUnit") ?? "esriMeters", cancellationToken).ConfigureAwait(false);
            }

            if (IsSensorDependentOperation(operation))
            {
                return StandardErrorHelpers.CreateNotImplemented(
                    context,
                    "Sensor-dependent ImageServer mensuration requires sensor/DEM metadata that is not modeled on this service.");
            }

            var linearUnit = GetString(values, "linearUnit") ?? "esriMeters";
            var angularUnit = GetString(values, "angularUnit") ?? "esriDUDecimalDegrees";
            var areaUnit = GetString(values, "areaUnit") ?? "esriSquareMeters";

            var response = operation.ToLowerInvariant() switch
            {
                "esrimensurationpoint" => BuildPointResponse(raster.Name, fromGeometry.Points[0]),
                "esrimensurationdistanceandangle" => BuildDistanceResponse(raster.Name, fromGeometry.Points[0], toGeometry!.Value.Points[0], linearUnit, angularUnit),
                "esrimensurationareaandperimeter" => BuildAreaResponse(raster.Name, fromGeometry, linearUnit, areaUnit),
                "esrimensurationcentroid" => BuildPointResponse(raster.Name, CalculateCentroid(fromGeometry)),
                _ => null
            };

            if (response is null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, $"Unsupported measureOperation '{operation}'.");
            }

            stopwatch.Stop();
            ImageServerLog.MeasureCompleted(_logger, layerId, operation, stopwatch.Elapsed.TotalMilliseconds);
            scope.SetSuccess(1);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);
            return Results.Json(response, ImageServerJsonContext.Default.ImageServerMeasureResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.MeasureFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "ImageServer measure failed.");
        }
    }

    private static bool TryParseRequest(
        IReadOnlyDictionary<string, StringValues> values,
        int? serviceSrid,
        out string operation,
        out MeasureGeometry fromGeometry,
        out MeasureGeometry? toGeometry,
        out string? error)
    {
        operation = GetString(values, "measureOperation") ?? string.Empty;
        fromGeometry = default;
        toGeometry = null;
        error = null;

        if (string.IsNullOrWhiteSpace(operation))
        {
            error = "measureOperation parameter is required.";
            return false;
        }

        var geometryType = GetString(values, "geometryType") ?? "esriGeometryPoint";
        if (!TryParseGeometry(GetString(values, "fromGeometry"), geometryType, serviceSrid, "fromGeometry", out fromGeometry, out error))
        {
            return false;
        }

        var normalizedOperation = operation.ToLowerInvariant();
        var requiresToGeometry = normalizedOperation is "esrimensurationdistanceandangle" or
            "esrimensurationdistanceandangle3d" or
            "esrimensurationheightfrombaseandtop" or
            "esrimensurationheightfrombaseandtopshadow" or
            "esrimensurationheightfromtopandtopshadow";
        if (requiresToGeometry)
        {
            if (!TryParseGeometry(GetString(values, "toGeometry"), geometryType, serviceSrid, "toGeometry", out var parsedTo, out error))
            {
                return false;
            }

            toGeometry = parsedTo;
        }

        if ((normalizedOperation is "esrimensurationpoint" or "esrimensurationpoint3d" or
             "esrimensurationdistanceandangle" or "esrimensurationdistanceandangle3d") &&
            !string.Equals(fromGeometry.GeometryType, "esriGeometryPoint", StringComparison.OrdinalIgnoreCase))
        {
            error = "Point mensuration operations require geometryType=esriGeometryPoint.";
            return false;
        }

        if ((normalizedOperation is "esrimensurationareaandperimeter" or "esrimensurationareaandperimeter3d" or
             "esrimensurationcentroid" or "esrimensurationcentroid3d") &&
            string.Equals(fromGeometry.GeometryType, "esriGeometryPoint", StringComparison.OrdinalIgnoreCase))
        {
            error = "Area, perimeter, and centroid mensuration require polygon or envelope geometry.";
            return false;
        }

        return true;
    }

    private static bool TryParseGeometry(
        string? raw,
        string geometryType,
        int? serviceSrid,
        string parameterName,
        out MeasureGeometry geometry,
        out string? error)
    {
        geometry = default;
        error = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"{parameterName} parameter is required.";
            return false;
        }

        var normalizedType = NormalizeGeometryType(geometryType);
        if (normalizedType is null)
        {
            error = $"Unsupported geometryType '{geometryType}'.";
            return false;
        }

        var trimmed = raw.Trim();
        if (normalizedType == "esriGeometryPoint" && !trimmed.StartsWith('{'))
        {
            var parts = trimmed.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                error = $"{parameterName} point syntax must be x,y.";
                return false;
            }

            geometry = new MeasureGeometry([new MeasurePoint(x, y, null, serviceSrid)], normalizedType, serviceSrid);
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"{parameterName} must be a JSON object.";
                return false;
            }

            var srid = ReadWkid(root) ?? serviceSrid;
            if (normalizedType == "esriGeometryPoint")
            {
                if (!TryGetJsonDouble(root, "x", out var x) ||
                    !TryGetJsonDouble(root, "y", out var y))
                {
                    error = $"{parameterName} must include x and y.";
                    return false;
                }

                double? z = null;
                if (root.TryGetProperty("z", out var zElement) &&
                    zElement.ValueKind == JsonValueKind.Number &&
                    zElement.TryGetDouble(out var parsedZ))
                {
                    z = parsedZ;
                }

                geometry = new MeasureGeometry([new MeasurePoint(x, y, z, srid)], normalizedType, srid);
                return true;
            }

            if (normalizedType == "esriGeometryEnvelope")
            {
                if (!TryGetJsonDouble(root, "xmin", out var xmin) ||
                    !TryGetJsonDouble(root, "ymin", out var ymin) ||
                    !TryGetJsonDouble(root, "xmax", out var xmax) ||
                    !TryGetJsonDouble(root, "ymax", out var ymax))
                {
                    error = $"{parameterName} envelope must include xmin, ymin, xmax, and ymax.";
                    return false;
                }

                geometry = new MeasureGeometry(
                    [
                        new MeasurePoint(xmin, ymin, null, srid),
                        new MeasurePoint(xmin, ymax, null, srid),
                        new MeasurePoint(xmax, ymax, null, srid),
                        new MeasurePoint(xmax, ymin, null, srid),
                        new MeasurePoint(xmin, ymin, null, srid)
                    ],
                    normalizedType,
                    srid);
                return true;
            }

            if (!root.TryGetProperty("rings", out var rings) ||
                rings.ValueKind != JsonValueKind.Array ||
                rings.GetArrayLength() == 0)
            {
                error = $"{parameterName} polygon must include rings.";
                return false;
            }

            var points = new List<MeasurePoint>();
            foreach (var ring in rings.EnumerateArray())
            {
                if (ring.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var pair in ring.EnumerateArray())
                {
                    if (pair.ValueKind == JsonValueKind.Array &&
                        pair.GetArrayLength() >= 2 &&
                        pair[0].ValueKind == JsonValueKind.Number &&
                        pair[1].ValueKind == JsonValueKind.Number)
                    {
                        points.Add(new MeasurePoint(pair[0].GetDouble(), pair[1].GetDouble(), null, srid));
                    }
                }
            }

            if (points.Count < 4)
            {
                error = $"{parameterName} polygon must include at least four coordinates.";
                return false;
            }

            geometry = new MeasureGeometry([.. points], normalizedType, srid);
            return true;
        }
        catch (JsonException)
        {
            error = $"{parameterName} must be valid JSON.";
            return false;
        }
    }

    private static ImageServerMeasureResponse BuildPointResponse(string name, MeasurePoint point)
        => new()
        {
            Name = name,
            Point = new ImageServerMeasurePointResult { Value = ToResponsePoint(point) }
        };

    private static ImageServerMeasureResponse BuildDistanceResponse(
        string name,
        MeasurePoint from,
        MeasurePoint to,
        string linearUnit,
        string angularUnit)
    {
        var distanceMeters = CalculateDistanceMeters(from, to);
        var azimuthDegrees = CalculateAzimuthDegrees(from, to);
        var (distance, distanceUnit) = ConvertLinear(distanceMeters, linearUnit);
        var (angle, angleUnit) = ConvertAngular(azimuthDegrees, angularUnit);
        return new ImageServerMeasureResponse
        {
            Name = name,
            Distance = CreateValue(distance, distanceUnit),
            AzimuthAngle = CreateValue(angle, angleUnit),
        };
    }

    private static ImageServerMeasureResponse BuildAreaResponse(
        string name,
        MeasureGeometry geometry,
        string linearUnit,
        string areaUnit)
    {
        var areaSquareMeters = CalculateAreaSquareMeters(geometry.Points, geometry.Srid);
        var perimeterMeters = CalculatePerimeterMeters(geometry.Points);
        var (area, responseAreaUnit) = ConvertArea(areaSquareMeters, areaUnit);
        var (perimeter, responseLinearUnit) = ConvertLinear(perimeterMeters, linearUnit);
        return new ImageServerMeasureResponse
        {
            Name = name,
            Area = CreateValue(area, responseAreaUnit),
            Perimeter = CreateValue(perimeter, responseLinearUnit),
        };
    }

    /// <summary>
    /// DEM-backed height mensuration (#1879). Resolves the raster's associated DEM layer from
    /// <c>raster_sensor_metadata.dem_source</c>, samples the ground elevation at the base and top
    /// points, and returns their difference as the measured height. Returns 501 when no DEM is
    /// modeled for the raster, when the DEM source is not a resolvable layer id, when no elevation
    /// service is configured, or when either point falls outside the DEM coverage — never a faked
    /// value.
    /// </summary>
    private async Task<IResult> MeasureDemHeightAsync(
        HttpContext context,
        int layerId,
        RasterInfo raster,
        MeasurePoint basePoint,
        MeasurePoint topPoint,
        string linearUnit,
        CancellationToken cancellationToken)
    {
        const string SensorMetadataMissing =
            "Height mensuration requires DEM/sensor metadata that is not modeled for this raster.";

        if (_elevationService is null)
        {
            return StandardErrorHelpers.CreateNotImplemented(context, SensorMetadataMissing);
        }

        var metadata = await _rasterStore.GetSensorMetadataAsync([raster.Id], cancellationToken).ConfigureAwait(false);
        var sensor = metadata.TryGetValue(raster.Id, out var meta) ? meta : raster.SensorMetadata;
        var demSource = sensor?.DemSource;
        if (string.IsNullOrWhiteSpace(demSource) ||
            !int.TryParse(demSource, NumberStyles.Integer, CultureInfo.InvariantCulture, out var demLayerId))
        {
            // No DEM modeled (or a non-layer-id source we cannot resolve yet): be honest with 501.
            return StandardErrorHelpers.CreateNotImplemented(context, SensorMetadataMissing);
        }

        ElevationPointResult baseElevation;
        ElevationPointResult topElevation;
        try
        {
            baseElevation = await _elevationService.QueryPointAsync(
                demLayerId, basePoint.X, basePoint.Y, basePoint.Srid, RasterMergeStrategy.Newest, cancellationToken)
                .ConfigureAwait(false);
            topElevation = await _elevationService.QueryPointAsync(
                demLayerId, topPoint.X, topPoint.Y, topPoint.Srid, RasterMergeStrategy.Newest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ElevationQueryException ex)
        {
            ImageServerLog.InvalidMeasureParameters(_logger, layerId, ex.Message);
            return StandardErrorHelpers.CreateNotImplemented(context, SensorMetadataMissing);
        }

        if (baseElevation.Elevation is not { } baseValue || topElevation.Elevation is not { } topValue)
        {
            // The DEM does not cover one of the points: return 501 rather than a fabricated height.
            return StandardErrorHelpers.CreateNotImplemented(
                context,
                "Height mensuration could not sample the DEM at the supplied base/top points.");
        }

        var heightMeters = Math.Abs(topValue - baseValue);
        var (height, unit) = ConvertLinear(heightMeters, linearUnit);

        var sensorName = sensor?.SensorName ?? "Unknown";
        var response = new ImageServerMeasureResponse
        {
            Name = raster.Name,
            SensorName = sensorName,
            Height = CreateValue(height, unit),
        };

        ImageServerLog.MeasureCompleted(_logger, layerId, "esriMensurationHeightFromBaseAndTop", 0);
        return Results.Json(response, ImageServerJsonContext.Default.ImageServerMeasureResponse);
    }

    private static bool IsSensorDependentOperation(string operation)
    {
        var normalized = operation.ToLowerInvariant();
        return normalized.Contains("3d", StringComparison.Ordinal) ||
               normalized.Contains("height", StringComparison.Ordinal);
    }

    private static MeasurePoint CalculateCentroid(MeasureGeometry geometry)
    {
        var points = geometry.Points;
        var kind = ClassifyMeasureCrs(geometry.Srid, out _);

        // Area-weighted (shoelace) centroid, not the vertex mean. For geographic/Web-Mercator
        // geometry the centroid is computed in lon/lat with longitudes unwrapped about a reference
        // vertex so a dateline-crossing ring lands on the correct side of the globe (the vertex
        // mean of a ring spanning +179..-179 wrongly collapses to lon 0); the result is mapped back
        // to the input SRID. Projected geometry is centroided directly in its native coordinates.
        if (kind == MeasureCrsKind.ProjectedLinear)
        {
            var (px, py) = AreaWeightedCentroid(points.Select(static p => (p.X, p.Y)).ToArray());
            return new MeasurePoint(px, py, null, geometry.Srid);
        }

        var geographic = points.Select(p => ToGeographic(p, kind)).ToArray();
        var referenceLon = geographic.Length > 0 ? geographic[0].Lon : 0d;
        var unwrapped = geographic
            .Select(p => (X: referenceLon + NormalizeLongitudeDelta(p.Lon - referenceLon), Y: p.Lat))
            .ToArray();
        var (centroidLonUnwrapped, centroidLat) = AreaWeightedCentroid(unwrapped);
        var centroidLon = NormalizeLongitudeDelta(centroidLonUnwrapped);

        if (kind == MeasureCrsKind.WebMercator)
        {
            var (mx, my) = WebMercatorMath.LonLatToWebMercator(centroidLon, centroidLat);
            return new MeasurePoint(mx, my, null, geometry.Srid);
        }

        return new MeasurePoint(centroidLon, centroidLat, null, geometry.Srid);
    }

    // Area-weighted polygon centroid via the shoelace formula. Falls back to the vertex mean for a
    // degenerate (near-zero-area) ring to avoid division by zero.
    private static (double X, double Y) AreaWeightedCentroid((double X, double Y)[] ring)
    {
        var count = ring.Length > 1 && ring[0].X.Equals(ring[^1].X) && ring[0].Y.Equals(ring[^1].Y)
            ? ring.Length - 1
            : ring.Length;

        var signedArea = 0d;
        var cx = 0d;
        var cy = 0d;
        for (var i = 0; i < count; i++)
        {
            var j = (i + 1) % count;
            var cross = (ring[i].X * ring[j].Y) - (ring[j].X * ring[i].Y);
            signedArea += cross;
            cx += (ring[i].X + ring[j].X) * cross;
            cy += (ring[i].Y + ring[j].Y) * cross;
        }

        if (Math.Abs(signedArea) < 1e-12d)
        {
            var meanX = 0d;
            var meanY = 0d;
            for (var i = 0; i < count; i++)
            {
                meanX += ring[i].X;
                meanY += ring[i].Y;
            }

            return (meanX / count, meanY / count);
        }

        signedArea *= 0.5d;
        return (cx / (6d * signedArea), cy / (6d * signedArea));
    }

    // Classifies the measurement CRS and, for a projected (non-Web-Mercator) CRS, reports the factor
    // that converts its linear unit to meters. Reuses the canonical geographic/projected classifier
    // (SpatialReference.IsGeographic, which delegates to BoundingBox.IsGeographicSrid — the single
    // source of truth for the geographic EPSG list per #2732), the shared Web-Mercator SRID-alias
    // normalizer, and the shared linear-unit lookup — instead of the former hard-coded "== 4326"
    // gate. Note: the #2732 unification of the several divergent SRID allowlists is a separate,
    // out-of-scope effort; this handler simply consumes the canonical helpers.
    private static MeasureCrsKind ClassifyMeasureCrs(int? srid, out double metersPerLinearUnit)
    {
        metersPerLinearUnit = 1d;
        if (srid is not { } wkid)
        {
            // No SRID supplied: default to the WGS84 geographic ecosystem default.
            return MeasureCrsKind.Geographic;
        }

        if (SpatialReferenceExtensions.NormalizeWebMercatorSrid(wkid) == 3857)
        {
            return MeasureCrsKind.WebMercator;
        }

        if (CoreSpatialReference.Create(wkid).IsGeographic)
        {
            return MeasureCrsKind.Geographic;
        }

        // Projected, non-Web-Mercator (e.g. UTM, State Plane): planar distance in the CRS's linear
        // unit is correct, but the unit is not necessarily the metre — convert via the shared
        // linear-unit lookup (covers US survey-foot State Plane zones; returns 1.0 for metric CRSes).
        metersPerLinearUnit = CoordinateTransformer.LinearUnitToMeters(wkid);
        return MeasureCrsKind.ProjectedLinear;
    }

    private static (double Lon, double Lat) ToGeographic(MeasurePoint point, MeasureCrsKind kind)
        => kind == MeasureCrsKind.WebMercator
            ? WebMercatorMath.WebMercatorToLonLat(point.X, point.Y)
            : (point.X, point.Y);

    // Normalizes a longitude delta to the half-open interval (-180, 180], so a dateline-crossing
    // step (e.g. 179 deg -> -179 deg) is treated as +2 deg rather than a spurious -358 deg jump.
    private static double NormalizeLongitudeDelta(double deltaDegrees)
    {
        var normalized = deltaDegrees % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return normalized;
    }

    private static double HaversineMeters(double lon1, double lat1, double lon2, double lat2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dPhi = DegreesToRadians(lat2 - lat1);
        var dLambda = DegreesToRadians(NormalizeLongitudeDelta(lon2 - lon1));
        var a = (Math.Sin(dPhi / 2d) * Math.Sin(dPhi / 2d)) +
                (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dLambda / 2d) * Math.Sin(dLambda / 2d));
        return 2d * EarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
    }

    private static double CalculateDistanceMeters(MeasurePoint from, MeasurePoint to)
    {
        var kind = ClassifyMeasureCrs(from.Srid ?? to.Srid, out var metersPerUnit);
        if (kind == MeasureCrsKind.ProjectedLinear)
        {
            // Planar distance in the CRS linear unit, converted to meters.
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            return Math.Sqrt((dx * dx) + (dy * dy)) * metersPerUnit;
        }

        // Geodesic (haversine) ground distance for any geographic CRS (not just 4326) and for
        // Web-Mercator (unprojected first). This is the fix for planar map-unit distances being
        // reported as esriMeters — e.g. a 3857 segment at 60 deg N is ~2x its true ground length.
        var (lon1, lat1) = ToGeographic(from, kind);
        var (lon2, lat2) = ToGeographic(to, kind);
        return HaversineMeters(lon1, lat1, lon2, lat2);
    }

    private static double CalculatePerimeterMeters(IReadOnlyList<MeasurePoint> points)
    {
        var total = 0d;
        for (var i = 1; i < points.Count; i++)
        {
            total += CalculateDistanceMeters(points[i - 1], points[i]);
        }

        return total;
    }

    private static double CalculateAreaSquareMeters(IReadOnlyList<MeasurePoint> points, int? srid)
    {
        var kind = ClassifyMeasureCrs(srid, out var metersPerUnit);
        var transformed = kind == MeasureCrsKind.ProjectedLinear
            ? points.Select(point => (X: point.X * metersPerUnit, Y: point.Y * metersPerUnit)).ToArray()
            : ProjectGeographicRingToMeters(points.Select(point => ToGeographic(point, kind)).ToArray());

        var area = 0d;
        for (var i = 0; i < transformed.Length; i++)
        {
            var j = (i + 1) % transformed.Length;
            area += transformed[i].X * transformed[j].Y;
            area -= transformed[j].X * transformed[i].Y;
        }

        return Math.Abs(area) / 2d;
    }

    // Local equirectangular projection of a geographic ring to meters for the planar shoelace area.
    // Longitudes are unwrapped relative to the first vertex so an antimeridian-crossing ring is not
    // torn by a 358 deg jump (which otherwise yields a garbage shoelace area).
    private static (double X, double Y)[] ProjectGeographicRingToMeters((double Lon, double Lat)[] points)
    {
        var referenceLon = points.Length > 0 ? points[0].Lon : 0d;
        var lat0 = points.Average(static point => point.Lat);
        var cosLat0 = Math.Cos(DegreesToRadians(lat0));
        return points
            .Select(point =>
            {
                var unwrappedLon = referenceLon + NormalizeLongitudeDelta(point.Lon - referenceLon);
                return (
                    X: EarthRadiusMeters * DegreesToRadians(unwrappedLon) * cosLat0,
                    Y: EarthRadiusMeters * DegreesToRadians(point.Lat));
            })
            .ToArray();
    }

    private static double CalculateAzimuthDegrees(MeasurePoint from, MeasurePoint to)
    {
        var kind = ClassifyMeasureCrs(from.Srid ?? to.Srid, out _);
        if (kind == MeasureCrsKind.ProjectedLinear)
        {
            // Grid bearing, clockwise from grid north, for a projected CRS.
            var gridDegrees = RadiansToDegrees(Math.Atan2(to.X - from.X, to.Y - from.Y));
            return gridDegrees < 0d ? gridDegrees + 360d : gridDegrees;
        }

        // Great-circle initial bearing for geographic/Web-Mercator inputs — consistent with the
        // haversine distance returned in the same response. The cos(lat) terms supply the longitude
        // scaling the previous planar atan2(dx, dy) lacked (which reported 45 deg at 60 deg N for
        // dLon=dLat=1 deg regardless of latitude).
        var (lon1, lat1) = ToGeographic(from, kind);
        var (lon2, lat2) = ToGeographic(to, kind);
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var dLambda = DegreesToRadians(NormalizeLongitudeDelta(lon2 - lon1));
        var y = Math.Sin(dLambda) * Math.Cos(phi2);
        var x = (Math.Cos(phi1) * Math.Sin(phi2)) - (Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda));
        var bearing = RadiansToDegrees(Math.Atan2(y, x));
        return (bearing + 360d) % 360d;
    }

    private static ImageServerMeasureValue CreateValue(double value, string unit)
        => new()
        {
            Value = value,
            DisplayValue = value.ToString("G17", CultureInfo.InvariantCulture),
            Unit = unit
        };

    private static (double Value, string Unit) ConvertLinear(double meters, string requestedUnit)
    {
        var (unit, metersPerUnit) = requestedUnit.ToLowerInvariant() switch
        {
            "esriinches" => ("esriInches", 0.0254d),
            "esrifeet" => ("esriFeet", 0.3048d),
            "esriyards" => ("esriYards", 0.9144d),
            "esrimiles" => ("esriMiles", 1609.344d),
            "esrinauticalmiles" => ("esriNauticalMiles", 1852d),
            "esrimillimeters" => ("esriMillimeters", 0.001d),
            "esricentimeters" => ("esriCentimeters", 0.01d),
            "esridecimeters" => ("esriDecimeters", 0.1d),
            "esrikilometers" => ("esriKilometers", 1000d),
            _ => ("esriMeters", 1d)
        };

        return (meters / metersPerUnit, unit);
    }

    private static (double Value, string Unit) ConvertArea(double squareMeters, string requestedUnit)
    {
        var (unit, squareMetersPerUnit) = requestedUnit.ToLowerInvariant() switch
        {
            "esrisquareinches" => ("esriSquareInches", 0.00064516d),
            "esrisquarefeet" => ("esriSquareFeet", 0.09290304d),
            "esrisquareyards" => ("esriSquareYards", 0.83612736d),
            "esriacres" => ("esriAcres", 4046.8564224d),
            "esrisquaremiles" => ("esriSquareMiles", 2589988.110336d),
            "esrisquaremillimeters" => ("esriSquareMillimeters", 0.000001d),
            "esrisquarecentimeters" => ("esriSquareCentimeters", 0.0001d),
            "esrisquaredecimeters" => ("esriSquareDecimeters", 0.01d),
            "esriares" => ("esriAres", 100d),
            "esrihectares" => ("esriHectares", 10000d),
            "esrisquarekilometers" => ("esriSquareKilometers", 1_000_000d),
            _ => ("esriSquareMeters", 1d)
        };

        return (squareMeters / squareMetersPerUnit, unit);
    }

    private static (double Value, string Unit) ConvertAngular(double degrees, string requestedUnit)
        => requestedUnit.Equals("esriDURadians", StringComparison.OrdinalIgnoreCase)
            ? (DegreesToRadians(degrees), "esriDURadians")
            : (degrees, "esriDUDecimalDegrees");

    private static ImageServerMeasurePoint ToResponsePoint(MeasurePoint point)
        => new()
        {
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            SpatialReference = point.Srid.HasValue
                ? new SpatialReference { Wkid = point.Srid.Value, LatestWkid = point.Srid.Value }
                : null
        };

    private static string? NormalizeGeometryType(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "esrigeometrypoint" => "esriGeometryPoint",
            "esrigeometrypolygon" => "esriGeometryPolygon",
            "esrigeometryenvelope" => "esriGeometryEnvelope",
            _ => null
        };
    }

    private static int? ReadWkid(JsonElement root)
    {
        if (!root.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sr.TryGetProperty("latestWkid", out var latest) &&
            latest.ValueKind == JsonValueKind.Number &&
            latest.TryGetInt32(out var latestWkid))
        {
            return latestWkid;
        }

        if (sr.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number &&
            wkid.TryGetInt32(out var wkidValue))
        {
            return wkidValue;
        }

        return null;
    }

    private static bool TryGetJsonDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0d;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value);
    }

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians)
        => radians * 180d / Math.PI;

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}

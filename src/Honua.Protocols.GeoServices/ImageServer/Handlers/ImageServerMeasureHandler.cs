// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using MeasureSpace = Honua.Protocols.GeoServices.ImageServer.Services.ImageServerMensurationMath.MeasureSpace;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Basic ImageServer mensuration. Distance, perimeter, area, and azimuth are
/// returned as ground quantities: coordinates are normalized to lon/lat (exact inverse Web
/// Mercator for EPSG:3857 and its aliases, identity for geographic SRIDs, or an authoritative
/// PostGIS transform for other projected SRIDs when a transform service is available) and then
/// measured geodesically. Projected SRIDs without an in-process inverse or reachable transform
/// service fall back to honest planar-meter math, which is only correct when the map units are
/// already meters (#2734).
/// </summary>
internal sealed class ImageServerMeasureHandler
{
    private readonly record struct MeasurePoint(double X, double Y, double? Z, int? Srid);

    private readonly record struct MeasureGeometry(
        MeasurePoint[] Points,
        string GeometryType,
        int? Srid);

    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerMeasureHandler> _logger;
    private readonly IElevationService? _elevationService;
    private readonly ICoordinateTransformService? _transformService;

    public ImageServerMeasureHandler(
        IRasterStore rasterStore,
        ILogger<ImageServerMeasureHandler> logger,
        IElevationService? elevationService = null,
        ICoordinateTransformService? transformService = null)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _elevationService = elevationService;
        _transformService = transformService;
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
                "esrimensurationdistanceandangle" => await BuildDistanceResponseAsync(
                    raster.Name, fromGeometry.Points[0], toGeometry!.Value.Points[0], linearUnit, angularUnit, cancellationToken).ConfigureAwait(false),
                "esrimensurationareaandperimeter" => await BuildAreaResponseAsync(
                    raster.Name, fromGeometry, linearUnit, areaUnit, cancellationToken).ConfigureAwait(false),
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

    private async ValueTask<ImageServerMeasureResponse> BuildDistanceResponseAsync(
        string name,
        MeasurePoint from,
        MeasurePoint to,
        string linearUnit,
        string angularUnit,
        CancellationToken cancellationToken)
    {
        var fromNormalized = await NormalizePointAsync(from, cancellationToken).ConfigureAwait(false);
        var toNormalized = await NormalizePointAsync(to, cancellationToken).ConfigureAwait(false);

        double distanceMeters;
        double azimuthDegrees;
        if (fromNormalized.Space == MeasureSpace.Geodesic && toNormalized.Space == MeasureSpace.Geodesic)
        {
            distanceMeters = ImageServerMensurationMath.GeodesicDistanceMeters(
                fromNormalized.X, fromNormalized.Y, toNormalized.X, toNormalized.Y);
            azimuthDegrees = ImageServerMensurationMath.InitialBearingDegrees(
                fromNormalized.X, fromNormalized.Y, toNormalized.X, toNormalized.Y);
        }
        else
        {
            // Projected map units already in meters (or unknown SRID): planar fallback on the
            // original coordinates. Both operands share the request's geometryType/SRID, so a
            // space mismatch is a corner case handled conservatively as planar.
            distanceMeters = ImageServerMensurationMath.PlanarDistanceMeters(from.X, from.Y, to.X, to.Y);
            azimuthDegrees = ImageServerMensurationMath.PlanarBearingDegrees(from.X, from.Y, to.X, to.Y);
        }

        var (distance, distanceUnit) = ConvertLinear(distanceMeters, linearUnit);
        var (angle, angleUnit) = ConvertAngular(azimuthDegrees, angularUnit);
        return new ImageServerMeasureResponse
        {
            Name = name,
            Distance = CreateValue(distance, distanceUnit),
            AzimuthAngle = CreateValue(angle, angleUnit),
        };
    }

    private async ValueTask<ImageServerMeasureResponse> BuildAreaResponseAsync(
        string name,
        MeasureGeometry geometry,
        string linearUnit,
        string areaUnit,
        CancellationToken cancellationToken)
    {
        var (coordinates, space) = await NormalizeRingAsync(geometry, cancellationToken).ConfigureAwait(false);

        double areaSquareMeters;
        double perimeterMeters;
        if (space == MeasureSpace.Geodesic)
        {
            areaSquareMeters = ImageServerMensurationMath.GeodesicRingAreaSquareMeters(coordinates);
            perimeterMeters = GeodesicPerimeterMeters(coordinates);
        }
        else
        {
            areaSquareMeters = ImageServerMensurationMath.PlanarRingAreaSquareMeters(coordinates);
            perimeterMeters = PlanarPerimeterMeters(coordinates);
        }

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
        var ring = new (double X, double Y)[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            ring[i] = (points[i].X, points[i].Y);
        }

        // Centroid is returned in the request's spatial reference, so it is computed directly
        // in the input coordinate space (the signed-area / shoelace centroid, not a vertex mean).
        // Geographic inputs unwrap longitudes so antimeridian-crossing rings do not skew the result.
        var unwrapLongitudes = geometry.Srid is int srid && ImageServerMensurationMath.IsGeographicSrid(srid);
        var (centroidX, centroidY) = ImageServerMensurationMath.SignedAreaCentroid(ring, unwrapLongitudes);
        return new MeasurePoint(centroidX, centroidY, null, geometry.Srid);
    }

    private static double GeodesicPerimeterMeters((double X, double Y)[] ring)
    {
        var total = 0d;
        for (var i = 1; i < ring.Length; i++)
        {
            total += ImageServerMensurationMath.GeodesicDistanceMeters(
                ring[i - 1].X, ring[i - 1].Y, ring[i].X, ring[i].Y);
        }

        return total;
    }

    private static double PlanarPerimeterMeters((double X, double Y)[] ring)
    {
        var total = 0d;
        for (var i = 1; i < ring.Length; i++)
        {
            total += ImageServerMensurationMath.PlanarDistanceMeters(
                ring[i - 1].X, ring[i - 1].Y, ring[i].X, ring[i].Y);
        }

        return total;
    }

    /// <summary>
    /// Normalizes a single measure point to a measurement coordinate: lon/lat degrees with
    /// <see cref="MeasureSpace.Geodesic"/> when the SRID is Web Mercator, geographic, or
    /// transformable to WGS 84; otherwise the original projected meters with
    /// <see cref="MeasureSpace.PlanarMeters"/>.
    /// </summary>
    private async ValueTask<(double X, double Y, MeasureSpace Space)> NormalizePointAsync(
        MeasurePoint point,
        CancellationToken cancellationToken)
    {
        if (point.Srid is int srid)
        {
            if (ImageServerMensurationMath.TryConvertToLonLat(point.X, point.Y, srid, out var lon, out var lat))
            {
                return (lon, lat, MeasureSpace.Geodesic);
            }

            if (_transformService is not null)
            {
                var transformed = await _transformService
                    .TransformPointAsync(point.X, point.Y, srid, 4326, cancellationToken)
                    .ConfigureAwait(false);
                if (transformed.HasValue)
                {
                    return (transformed.Value.X, transformed.Value.Y, MeasureSpace.Geodesic);
                }
            }
        }

        return (point.X, point.Y, MeasureSpace.PlanarMeters);
    }

    /// <summary>
    /// Normalizes a ring to measurement coordinates, preferring the exact in-process lon/lat
    /// conversion, then an authoritative batch transform to WGS 84 when a transform service is
    /// available, and finally the honest planar-meter fallback for other projected SRIDs.
    /// </summary>
    private async ValueTask<((double X, double Y)[] Coordinates, MeasureSpace Space)> NormalizeRingAsync(
        MeasureGeometry geometry,
        CancellationToken cancellationToken)
    {
        var points = geometry.Points;
        if (geometry.Srid is int srid)
        {
            if (points.Length > 0 &&
                ImageServerMensurationMath.TryConvertToLonLat(points[0].X, points[0].Y, srid, out _, out _))
            {
                var coordinates = new (double X, double Y)[points.Length];
                for (var i = 0; i < points.Length; i++)
                {
                    ImageServerMensurationMath.TryConvertToLonLat(points[i].X, points[i].Y, srid, out var lon, out var lat);
                    coordinates[i] = (lon, lat);
                }

                return (coordinates, MeasureSpace.Geodesic);
            }

            if (_transformService is not null && points.Length > 0)
            {
                var xs = new double[points.Length];
                var ys = new double[points.Length];
                for (var i = 0; i < points.Length; i++)
                {
                    xs[i] = points[i].X;
                    ys[i] = points[i].Y;
                }

                if (await _transformService.TransformPointsAsync(xs, ys, srid, 4326, cancellationToken).ConfigureAwait(false))
                {
                    var coordinates = new (double X, double Y)[points.Length];
                    for (var i = 0; i < points.Length; i++)
                    {
                        coordinates[i] = (xs[i], ys[i]);
                    }

                    return (coordinates, MeasureSpace.Geodesic);
                }
            }
        }

        var planar = new (double X, double Y)[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            planar[i] = (points[i].X, points[i].Y);
        }

        return (planar, MeasureSpace.PlanarMeters);
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

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}

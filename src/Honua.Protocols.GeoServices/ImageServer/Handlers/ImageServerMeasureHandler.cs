// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for Basic ImageServer mensuration.
/// </summary>
internal sealed class ImageServerMeasureHandler
{
    private const double EarthRadiusMeters = 6378137d;

    private readonly record struct MeasurePoint(double X, double Y, double? Z, int? Srid);

    private readonly record struct MeasureGeometry(
        MeasurePoint[] Points,
        string GeometryType,
        int? Srid);

    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerMeasureHandler> _logger;

    public ImageServerMeasureHandler(
        IRasterStore rasterStore,
        ILogger<ImageServerMeasureHandler> logger)
    {
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    private static bool IsSensorDependentOperation(string operation)
    {
        var normalized = operation.ToLowerInvariant();
        return normalized.Contains("3d", StringComparison.Ordinal) ||
               normalized.Contains("height", StringComparison.Ordinal);
    }

    private static MeasurePoint CalculateCentroid(MeasureGeometry geometry)
    {
        var points = geometry.Points;
        var count = points.Length > 1 && SamePoint(points[0], points[^1]) ? points.Length - 1 : points.Length;
        var x = 0d;
        var y = 0d;
        for (var i = 0; i < count; i++)
        {
            x += points[i].X;
            y += points[i].Y;
        }

        return new MeasurePoint(x / count, y / count, null, geometry.Srid);
    }

    private static double CalculateDistanceMeters(MeasurePoint from, MeasurePoint to)
    {
        if ((from.Srid ?? to.Srid) == 4326)
        {
            var lat1 = DegreesToRadians(from.Y);
            var lat2 = DegreesToRadians(to.Y);
            var dLat = DegreesToRadians(to.Y - from.Y);
            var dLon = DegreesToRadians(to.X - from.X);
            var a = Math.Sin(dLat / 2d) * Math.Sin(dLat / 2d) +
                    Math.Cos(lat1) * Math.Cos(lat2) *
                    Math.Sin(dLon / 2d) * Math.Sin(dLon / 2d);
            return 2d * EarthRadiusMeters * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        }

        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
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
        var transformed = srid == 4326
            ? ProjectGeographicRingToMeters(points)
            : points.Select(static point => (point.X, point.Y)).ToArray();

        var area = 0d;
        for (var i = 0; i < transformed.Length; i++)
        {
            var j = (i + 1) % transformed.Length;
            area += transformed[i].X * transformed[j].Y;
            area -= transformed[j].X * transformed[i].Y;
        }

        return Math.Abs(area) / 2d;
    }

    private static (double X, double Y)[] ProjectGeographicRingToMeters(IReadOnlyList<MeasurePoint> points)
    {
        var lat0 = points.Average(static point => point.Y);
        var cosLat0 = Math.Cos(DegreesToRadians(lat0));
        return points
            .Select(point => (
                X: EarthRadiusMeters * DegreesToRadians(point.X) * cosLat0,
                Y: EarthRadiusMeters * DegreesToRadians(point.Y)))
            .ToArray();
    }

    private static double CalculateAzimuthDegrees(MeasurePoint from, MeasurePoint to)
    {
        var radians = Math.Atan2(to.X - from.X, to.Y - from.Y);
        var degrees = RadiansToDegrees(radians);
        return degrees < 0 ? degrees + 360d : degrees;
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

    private static bool SamePoint(MeasurePoint left, MeasurePoint right)
        => left.X.Equals(right.X) && left.Y.Equals(right.Y);

    private static double DegreesToRadians(double degrees)
        => degrees * Math.PI / 180d;

    private static double RadiansToDegrees(double radians)
        => radians * 180d / Math.PI;

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}

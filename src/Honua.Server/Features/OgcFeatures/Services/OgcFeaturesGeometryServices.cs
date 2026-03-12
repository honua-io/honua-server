// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.Extensions.Options;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.OgcFeatures.Services;

/// <summary>
/// Provides geometry processing, conversion, and validation services for OGC Features.
/// </summary>
internal sealed partial class OgcFeaturesGeometryServices
{
    private readonly GeometryLimits _geometryLimits;
    private readonly ILogger<OgcFeaturesGeometryServices> _logger;

    public OgcFeaturesGeometryServices(
        IOptions<LimitsOptions> limitsOptions,
        ILogger<OgcFeaturesGeometryServices> logger)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _logger = logger;
    }

    /// <summary>
    /// Result of geometry WKB creation operation.
    /// </summary>
    public sealed class WkbCreationResult
    {
        public bool IsSuccess { get; init; }
        public byte[]? Wkb { get; init; }
        public string? ErrorMessage { get; init; }

        public static WkbCreationResult Success(byte[] wkb) => new() { IsSuccess = true, Wkb = wkb };
        public static WkbCreationResult Failure(string errorMessage) => new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Converts WKB geometry to simple GeoJSON geometry with proper axis ordering.
    /// </summary>
    public SimpleGeoJsonGeometry? ConvertWkbToSimpleGeometry(byte[]? wkb, AxisOrder axisOrder)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return null;
        }

        var reader = new WKBReader();
        var geometry = reader.Read(wkb);
        if (geometry == null)
        {
            return null;
        }

        return ConvertGeometryToSimpleGeometry(geometry, axisOrder);
    }

    /// <summary>
    /// Converts a GeoJSON geometry fragment to simple GeoJSON geometry with proper axis ordering.
    /// </summary>
    /// <param name="geoJson">GeoJSON geometry fragment.</param>
    /// <param name="axisOrder">Requested output axis order.</param>
    /// <returns>Simple geometry representation, or null when the input cannot be parsed.</returns>
    public SimpleGeoJsonGeometry? ConvertGeoJsonToSimpleGeometry(string? geoJson, AxisOrder axisOrder)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            return null;
        }

        try
        {
            var reader = new GeoJsonReader();
            var geometry = reader.Read<Geometry>(geoJson);
            if (geometry == null)
            {
                return null;
            }

            return ConvertGeometryToSimpleGeometry(geometry, axisOrder);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException or Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private SimpleGeoJsonGeometry ConvertGeometryToSimpleGeometry(Geometry geometry, AxisOrder axisOrder)
    {
        if (axisOrder == AxisOrder.NorthEast)
        {
            geometry = (Geometry)geometry.Copy();
            geometry.Apply(new AxisSwapFilter());
            geometry.GeometryChanged();
        }

        geometry = GeometryOutputProcessor.ApplyLimits(geometry, _geometryLimits) ?? geometry;

        var writer = new GeoJsonWriter();
        var geoJson = writer.Write(geometry);

        using var document = JsonDocument.Parse(geoJson);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "Geometry";

        string? coordinatesJson = null;
        string? geometriesJson = null;

        if (root.TryGetProperty("coordinates", out var coordinates))
        {
            coordinatesJson = coordinates.GetRawText();
        }

        if (root.TryGetProperty("geometries", out var geometries))
        {
            geometriesJson = geometries.GetRawText();
        }

        return new SimpleGeoJsonGeometry
        {
            Type = type,
            CoordinatesJson = coordinatesJson,
            GeometriesJson = geometriesJson
        };
    }

    /// <summary>
    /// Creates WKB from GeoJSON geometry with validation.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Kept as an instance method to preserve the current geometry service API.")]
    public WkbCreationResult TryCreateWkbFromGeoJson(
        SimpleGeoJsonGeometry geometry,
        int srid,
        AxisOrder axisOrder = AxisOrder.EastNorth)
    {
        var coordinatesJson = geometry.CoordinatesJson;
        var geometriesJson = geometry.GeometriesJson;

        if (string.IsNullOrWhiteSpace(coordinatesJson) && string.IsNullOrWhiteSpace(geometriesJson))
        {
            return WkbCreationResult.Failure("Geometry coordinates are required.");
        }

        var json = coordinatesJson is not null
            ? $"{{\"type\":\"{geometry.Type}\",\"coordinates\":{coordinatesJson}}}"
            : $"{{\"type\":\"{geometry.Type}\",\"geometries\":{geometriesJson}}}";

        try
        {
            var reader = new GeoJsonReader();
            var ntsGeometry = reader.Read<Geometry>(json);
            if (ntsGeometry == null)
            {
                return WkbCreationResult.Failure("Invalid geometry.");
            }

            if (axisOrder == AxisOrder.NorthEast)
            {
                ntsGeometry = (Geometry)ntsGeometry.Copy();
                ntsGeometry.Apply(new AxisSwapFilter());
                ntsGeometry.GeometryChanged();
            }

            if (srid > 0)
            {
                ntsGeometry.SRID = srid;
            }

            var (hasZ, hasM) = GetHasZandM(ntsGeometry);
            var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: srid > 0, emitZ: hasZ, emitM: hasM);
            var wkb = writer.Write(ntsGeometry);
            return WkbCreationResult.Success(wkb);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            return WkbCreationResult.Failure("Invalid geometry.");
        }
    }

    /// <summary>
    /// Validates geometry complexity and ensures it meets system limits.
    /// </summary>
    public static bool ValidateGeometryComplexity(Geometry geometry, int maxVertices = 10000)
    {
        if (geometry == null)
        {
            return true;
        }

        var vertexCount = CountVertices(geometry);
        return vertexCount <= maxVertices;
    }

    /// <summary>
    /// Counts total vertices in a geometry including nested geometries.
    /// </summary>
    public static int CountVertices(Geometry geometry)
    {
        return geometry switch
        {
            Point => 1,
            LineString lineString => lineString.NumPoints,
            Polygon polygon => CountPolygonVertices(polygon),
            MultiPoint multiPoint => multiPoint.NumGeometries,
            MultiLineString multiLineString => multiLineString.Geometries.Cast<LineString>().Sum(ls => ls.NumPoints),
            MultiPolygon multiPolygon => multiPolygon.Geometries.Cast<Polygon>().Sum(CountPolygonVertices),
            GeometryCollection collection => collection.Geometries.Sum(CountVertices),
            _ => 0
        };
    }

    /// <summary>
    /// Simplifies geometry if it exceeds complexity thresholds.
    /// </summary>
    public Geometry? SimplifyIfNeeded(Geometry? geometry, double tolerance = 0.0001)
    {
        if (geometry == null)
        {
            return geometry;
        }

        // Check if simplification is needed based on vertex count
        const int maxVerticesBeforeSimplification = 5000;
        if (CountVertices(geometry) <= maxVerticesBeforeSimplification)
        {
            return geometry;
        }

        try
        {
            var simplified = NetTopologySuite.Simplify.TopologyPreservingSimplifier.Simplify(geometry, tolerance);
            return simplified ?? geometry;
        }
        catch (Exception ex)
        {
            Log.GeometrySimplificationFailed(_logger, tolerance, ex);
            return geometry;
        }
    }

    /// <summary>
    /// Validates that geometry is topologically valid.
    /// </summary>
    public bool ValidateTopology(Geometry geometry)
    {
        if (geometry == null)
        {
            return true;
        }

        try
        {
            return geometry.IsValid;
        }
        catch (Exception ex)
        {
            Log.TopologyValidationFailed(_logger, ex);
            return false;
        }
    }

    /// <summary>
    /// Repairs invalid geometry if possible.
    /// </summary>
    public Geometry? RepairGeometry(Geometry geometry)
    {
        if (geometry == null || geometry.IsValid)
        {
            return geometry;
        }

        try
        {
            // Try to repair using buffer(0) technique
            var buffered = geometry.Buffer(0);
            if (buffered?.IsValid == true)
            {
                return buffered;
            }

            // If buffer doesn't work, try convex hull as last resort
            var convexHull = geometry.ConvexHull();
            return convexHull?.IsValid == true ? convexHull : null;
        }
        catch (Exception ex)
        {
            Log.GeometryRepairFailed(_logger, ex);
            return null;
        }
    }

    /// <summary>
    /// Extracts coordinate dimension information from geometry.
    /// </summary>
    public static (bool hasZ, bool hasM) GetHasZandM(Geometry geometry)
    {
        return Infrastructure.Services.GeometryService.DetectZMFromGeometry(geometry);
    }

    /// <summary>
    /// Computes the bounding box of a geometry in the specified CRS.
    /// </summary>
    public static Envelope? GetBoundingBox(Geometry geometry)
    {
        return geometry?.EnvelopeInternal;
    }

    /// <summary>
    /// Transforms geometry from one SRID to another.
    /// </summary>
    public static Geometry? TransformGeometry(Geometry geometry, int fromSrid, int toSrid)
    {
        if (geometry == null || fromSrid == toSrid)
        {
            return geometry;
        }

        throw new NotSupportedException("In-memory CRS transforms are not supported. Use PostGIS ST_Transform.");
    }

    private static int CountPolygonVertices(Polygon polygon)
    {
        var count = polygon.ExteriorRing?.NumPoints ?? 0;
        for (int i = 0; i < polygon.NumInteriorRings; i++)
        {
            count += polygon.GetInteriorRingN(i).NumPoints;
        }
        return count;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5460, Level = LogLevel.Debug, Message = "Geometry simplification failed with tolerance {Tolerance}, returning original geometry")]
        public static partial void GeometrySimplificationFailed(ILogger logger, double tolerance, Exception exception);

        [LoggerMessage(EventId = 5461, Level = LogLevel.Debug, Message = "Topology validation failed, treating geometry as invalid")]
        public static partial void TopologyValidationFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 5462, Level = LogLevel.Debug, Message = "Geometry repair failed, returning null")]
        public static partial void GeometryRepairFailed(ILogger logger, Exception exception);
    }

}

/// <summary>
/// Coordinate sequence filter that swaps X and Y coordinates for axis order conversion.
/// </summary>
internal sealed class AxisSwapFilter : ICoordinateSequenceFilter
{
    public bool Done => false;

    public bool GeometryChanged => true;

    public void Filter(CoordinateSequence seq, int i)
    {
        var x = seq.GetX(i);
        var y = seq.GetY(i);
        seq.SetX(i, y);
        seq.SetY(i, x);
    }
}

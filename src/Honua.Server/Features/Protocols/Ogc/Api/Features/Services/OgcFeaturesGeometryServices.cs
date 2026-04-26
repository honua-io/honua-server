// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Buffers;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Geometries;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Microsoft.Extensions.Options;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// Provides geometry processing, conversion, and validation services for OGC Features.
/// </summary>
internal sealed partial class OgcFeaturesGeometryServices
{
    private readonly IGeometryService _geometryService;
    private readonly ICoordinateTransformService _coordinateTransformService;
    private readonly GeometryLimits _geometryLimits;
    private readonly ILogger<OgcFeaturesGeometryServices> _logger;

    public OgcFeaturesGeometryServices(
        IGeometryService geometryService,
        ICoordinateTransformService coordinateTransformService,
        IOptions<LimitsOptions> limitsOptions,
        ILogger<OgcFeaturesGeometryServices> logger)
    {
        _geometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        _coordinateTransformService = coordinateTransformService ?? throw new ArgumentNullException(nameof(coordinateTransformService));
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _logger = logger;
    }

    public bool CanUsePreformattedGeoJson => _geometryLimits.SimplifyTolerance is not > 0;

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
            using var document = JsonDocument.Parse(geoJson);
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String)
            {
                var type = typeElement.GetString();
                if (string.IsNullOrWhiteSpace(type))
                {
                    return null;
                }

                var validationResult = OgcGeoJsonGeometryShapeValidator.GetValidationResult(root, type);
                if (validationResult == GeometryShapeValidationResult.Invalid)
                {
                    return null;
                }

                if (validationResult == GeometryShapeValidationResult.Valid &&
                    TryConvertGeoJsonWithoutNts(root, type, axisOrder, out var fastPathGeometry))
                {
                    return fastPathGeometry;
                }
            }

            var reader = new GeoJsonReader();
            var geometry = reader.Read<Geometry>(geoJson);
            if (geometry == null)
            {
                return null;
            }

            return ConvertGeometryToSimpleGeometry(geometry, axisOrder);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or InvalidOperationException or JsonException or Newtonsoft.Json.JsonException)
        {
            return null;
        }
    }

    private bool TryConvertGeoJsonWithoutNts(
        JsonElement root,
        string type,
        AxisOrder axisOrder,
        out SimpleGeoJsonGeometry? geometry)
    {
        geometry = null;

        if (_geometryLimits.SimplifyTolerance is > 0)
        {
            return false;
        }

        var precision = Math.Clamp(_geometryLimits.MaxCoordinatePrecision, 0, 15);
        string? coordinatesJson = null;
        string? geometriesJson = null;

        if (root.TryGetProperty("coordinates", out var coordinatesElement))
        {
            coordinatesJson = RewriteCoordinatesJson(coordinatesElement, axisOrder, precision);
        }

        if (root.TryGetProperty("geometries", out var geometriesElement))
        {
            geometriesJson = RewriteGeometriesJson(geometriesElement, axisOrder, precision);
        }

        geometry = new SimpleGeoJsonGeometry
        {
            Type = type,
            CoordinatesJson = coordinatesJson,
            GeometriesJson = geometriesJson
        };
        return true;
    }

    private static string RewriteCoordinatesJson(
        JsonElement coordinatesElement,
        AxisOrder axisOrder,
        int precision)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCoordinateNode(writer, coordinatesElement, axisOrder, precision);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string RewriteGeometriesJson(
        JsonElement geometriesElement,
        AxisOrder axisOrder,
        int precision)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var geometryElement in geometriesElement.EnumerateArray())
        {
            WriteGeometryElement(writer, geometryElement, axisOrder, precision);
        }
        writer.WriteEndArray();
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteGeometryElement(
        Utf8JsonWriter writer,
        JsonElement geometryElement,
        AxisOrder axisOrder,
        int precision)
    {
        writer.WriteStartObject();

        foreach (var property in geometryElement.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (property.NameEquals("coordinates"))
            {
                WriteCoordinateNode(writer, property.Value, axisOrder, precision);
            }
            else if (property.NameEquals("geometries"))
            {
                writer.WriteStartArray();
                foreach (var childGeometry in property.Value.EnumerateArray())
                {
                    WriteGeometryElement(writer, childGeometry, axisOrder, precision);
                }
                writer.WriteEndArray();
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteCoordinateNode(
        Utf8JsonWriter writer,
        JsonElement node,
        AxisOrder axisOrder,
        int precision)
    {
        if (node.ValueKind != JsonValueKind.Array)
        {
            node.WriteTo(writer);
            return;
        }

        if (IsPositionArray(node))
        {
            WritePosition(writer, node, axisOrder, precision);
            return;
        }

        writer.WriteStartArray();
        foreach (var child in node.EnumerateArray())
        {
            WriteCoordinateNode(writer, child, axisOrder, precision);
        }
        writer.WriteEndArray();
    }

    private static bool IsPositionArray(JsonElement node)
    {
        using var enumerator = node.EnumerateArray();
        if (!enumerator.MoveNext())
        {
            return false;
        }

        return enumerator.Current.ValueKind == JsonValueKind.Number;
    }

    private static void WritePosition(
        Utf8JsonWriter writer,
        JsonElement positionElement,
        AxisOrder axisOrder,
        int precision)
    {
        var ordinates = new List<double>(4);
        foreach (var ordinate in positionElement.EnumerateArray())
        {
            if (ordinate.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            ordinates.Add(Math.Round(ordinate.GetDouble(), precision, MidpointRounding.AwayFromZero));
        }

        writer.WriteStartArray();
        for (var i = 0; i < ordinates.Count; i++)
        {
            var sourceIndex = axisOrder == AxisOrder.NorthEast && ordinates.Count >= 2
                ? i switch
                {
                    0 => 1,
                    1 => 0,
                    _ => i
                }
                : i;
            writer.WriteNumberValue(ordinates[sourceIndex]);
        }
        writer.WriteEndArray();
    }

    private SimpleGeoJsonGeometry ConvertGeometryToSimpleGeometry(Geometry geometry, AxisOrder axisOrder)
    {
        if (axisOrder == AxisOrder.NorthEast)
        {
            geometry = (Geometry)geometry.Copy();
            geometry.Apply(new AxisSwapCoordinateFilter());
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
        var parseResult = TryCreateGeometryFromGeoJson(geometry, srid, axisOrder);
        if (!parseResult.IsSuccess || parseResult.Geometry == null)
        {
            return WkbCreationResult.Failure(parseResult.ErrorMessage ?? "Invalid geometry.");
        }

        return WriteGeometryAsWkb(parseResult.Geometry, srid);
    }

    /// <summary>
    /// Creates WKB from GeoJSON geometry and transforms it into the target SRID when necessary.
    /// </summary>
    public async Task<WkbCreationResult> TryCreateWkbFromGeoJsonAsync(
        SimpleGeoJsonGeometry geometry,
        int inputSrid,
        int targetSrid,
        AxisOrder axisOrder = AxisOrder.EastNorth,
        CancellationToken cancellationToken = default)
    {
        var parseResult = TryCreateGeometryFromGeoJson(geometry, inputSrid, axisOrder);
        if (!parseResult.IsSuccess || parseResult.Geometry == null)
        {
            return WkbCreationResult.Failure(parseResult.ErrorMessage ?? "Invalid geometry.");
        }

        var ntsGeometry = parseResult.Geometry;
        if (inputSrid > 0 && targetSrid > 0 && inputSrid != targetSrid)
        {
            ntsGeometry = (Geometry)ntsGeometry.Copy();
            if (!await TryTransformGeometryAsync(ntsGeometry, inputSrid, targetSrid, cancellationToken).ConfigureAwait(false))
            {
                return WkbCreationResult.Failure(
                    $"Unable to transform geometry from SRID {inputSrid} to SRID {targetSrid}.");
            }
        }

        return WriteGeometryAsWkb(ntsGeometry, targetSrid > 0 ? targetSrid : inputSrid);
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

    private async Task<bool> TryTransformGeometryAsync(
        Geometry geometry,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        if (fromSrid == toSrid)
        {
            SetGeometrySridRecursive(geometry, toSrid);
            return true;
        }

        switch (geometry)
        {
            case Point point:
                return await TryTransformCoordinateSequenceAsync(point.CoordinateSequence, point, fromSrid, toSrid, cancellationToken).ConfigureAwait(false);
            case LinearRing linearRing:
                return await TryTransformCoordinateSequenceAsync(linearRing.CoordinateSequence, linearRing, fromSrid, toSrid, cancellationToken).ConfigureAwait(false);
            case LineString lineString:
                return await TryTransformCoordinateSequenceAsync(lineString.CoordinateSequence, lineString, fromSrid, toSrid, cancellationToken).ConfigureAwait(false);
            case Polygon polygon:
                if (!await TryTransformGeometryAsync(polygon.ExteriorRing, fromSrid, toSrid, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                for (var index = 0; index < polygon.NumInteriorRings; index++)
                {
                    if (!await TryTransformGeometryAsync(polygon.GetInteriorRingN(index), fromSrid, toSrid, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                polygon.SRID = toSrid;
                polygon.GeometryChanged();
                return true;
            case GeometryCollection geometryCollection:
                foreach (Geometry child in geometryCollection.Geometries)
                {
                    if (!await TryTransformGeometryAsync(child, fromSrid, toSrid, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }

                geometryCollection.SRID = toSrid;
                geometryCollection.GeometryChanged();
                return true;
            default:
                return false;
        }
    }

    private async Task<bool> TryTransformCoordinateSequenceAsync(
        CoordinateSequence sequence,
        Geometry geometry,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < sequence.Count; index++)
        {
            var transformed = await _coordinateTransformService.TransformPointAsync(
                sequence.GetX(index),
                sequence.GetY(index),
                fromSrid,
                toSrid,
                cancellationToken).ConfigureAwait(false);
            if (!transformed.HasValue)
            {
                return false;
            }

            sequence.SetX(index, transformed.Value.X);
            sequence.SetY(index, transformed.Value.Y);
        }

        geometry.SRID = toSrid;
        geometry.GeometryChanged();
        return true;
    }

    private static void SetGeometrySridRecursive(Geometry geometry, int srid)
    {
        geometry.SRID = srid;
        if (geometry is GeometryCollection geometryCollection)
        {
            foreach (Geometry child in geometryCollection.Geometries)
            {
                SetGeometrySridRecursive(child, srid);
            }
        }
    }

    private (bool IsSuccess, Geometry? Geometry, string? ErrorMessage) TryCreateGeometryFromGeoJson(
        SimpleGeoJsonGeometry geometry,
        int srid,
        AxisOrder axisOrder)
    {
        var coordinatesJson = geometry.CoordinatesJson;
        var geometriesJson = geometry.GeometriesJson;

        if (string.IsNullOrWhiteSpace(coordinatesJson) && string.IsNullOrWhiteSpace(geometriesJson))
        {
            return (false, null, "Geometry coordinates are required.");
        }

        var json = coordinatesJson is not null
            ? $"{{\"type\":\"{geometry.Type}\",\"coordinates\":{coordinatesJson}}}"
            : $"{{\"type\":\"{geometry.Type}\",\"geometries\":{geometriesJson}}}";

        try
        {
            var converted = _geometryService.ConvertGeoJsonToWkb(json, srid > 0 ? srid : null);
            if (converted == null || converted.Length == 0)
            {
                return (false, null, "Invalid geometry.");
            }

            var reader = new WKBReader();
            var ntsGeometry = reader.Read(converted);
            if (ntsGeometry == null)
            {
                return (false, null, "Invalid geometry.");
            }

            if (axisOrder == AxisOrder.NorthEast)
            {
                ntsGeometry = (Geometry)ntsGeometry.Copy();
                ntsGeometry.Apply(new AxisSwapCoordinateFilter());
                ntsGeometry.GeometryChanged();
            }

            if (srid > 0)
            {
                ntsGeometry.SRID = srid;
            }

            return (true, ntsGeometry, null);
        }
        catch (Exception ex) when (ex is ArgumentException or ParseException or FormatException or JsonException)
        {
            return (false, null, "Invalid geometry.");
        }
    }

    private static WkbCreationResult WriteGeometryAsWkb(Geometry geometry, int srid)
    {
        if (srid > 0)
        {
            geometry.SRID = srid;
        }

        var (hasZ, hasM) = GetHasZandM(geometry);
        var writer = new WKBWriter(ByteOrder.LittleEndian, handleSRID: srid > 0, emitZ: hasZ, emitM: hasM);
        var wkb = writer.Write(geometry);
        return WkbCreationResult.Success(wkb);
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

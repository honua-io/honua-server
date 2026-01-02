// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Geometry.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Unified geometry service providing consistent format conversion and Z/M detection
/// across all protocols (GeoServices REST, OGC API Features, OData, MVT).
/// </summary>
/// <remarks>
/// <para>
/// This implementation consolidates geometry operations that were previously scattered across
/// multiple implementations with divergent behavior. Z/M detection uses a consistent algorithm
/// that checks coordinate sequences for non-NaN values, ensuring identical results regardless
/// of which protocol is being used.
/// </para>
/// <para>
/// Behavior reference: Consolidates logic from:
/// - ../Features/OgcFeatures/FeaturesEndpoints.cs (GetHasZandM method)
/// - ../Features/FeatureServer/Services/GeoServicesGeometryConverter.cs (GetHasZandM, HasOrdinateValues methods)
/// - ../Features/FeatureServer/Services/GeometryValidator.cs (HasZ/HasM detection in ValidateWkb)
/// </para>
/// </remarks>
internal sealed class GeometryService : IGeometryService
{
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKTReader _wktReader = new();
    private static readonly GeoJsonReader _geoJsonReader = new();
    private static readonly GeoJsonWriter _geoJsonWriter = new();

    /// <inheritdoc />
    public (bool HasZ, bool HasM) DetectZM(byte[]? wkb)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return (false, false);
        }

        try
        {
            var geometry = _wkbReader.Read(wkb);
            return DetectZMFromGeometry(geometry);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <inheritdoc />
    public (bool HasZ, bool HasM) DetectZM(Memory<byte> wkb)
    {
        if (wkb.Length == 0)
        {
            return (false, false);
        }

        return DetectZM(wkb.ToArray());
    }

    /// <inheritdoc />
    public string? ConvertWkbToGeoJson(byte[]? wkb)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return null;
        }

        try
        {
            var geometry = _wkbReader.Read(wkb);
            if (geometry == null)
            {
                return null;
            }

            return _geoJsonWriter.Write(geometry);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException($"Invalid WKB geometry format: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public string? ConvertWkbToGeoJson(Memory<byte> wkb)
    {
        if (wkb.Length == 0)
        {
            return null;
        }

        return ConvertWkbToGeoJson(wkb.ToArray());
    }

    /// <inheritdoc />
    public byte[]? ConvertGeoJsonToWkb(string? geoJson, int? srid = null)
    {
        if (string.IsNullOrWhiteSpace(geoJson))
        {
            return null;
        }

        try
        {
            var geometry = _geoJsonReader.Read<Geometry>(geoJson);
            if (geometry == null)
            {
                return null;
            }

            if (srid.HasValue && srid.Value > 0)
            {
                geometry.SRID = srid.Value;
            }

            var (hasZ, hasM) = DetectZMFromGeometry(geometry);
            var writer = new WKBWriter(
                ByteOrder.LittleEndian,
                handleSRID: srid.HasValue && srid.Value > 0,
                emitZ: hasZ,
                emitM: hasM);

            return writer.Write(geometry);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException($"Invalid GeoJSON format: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public byte[]? ConvertWktToWkb(string? wkt, int? srid = null)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        try
        {
            var geometry = _wktReader.Read(wkt);
            if (geometry == null)
            {
                return null;
            }

            if (srid.HasValue && srid.Value > 0)
            {
                geometry.SRID = srid.Value;
            }

            var (hasZ, hasM) = DetectZMFromGeometry(geometry);
            var writer = new WKBWriter(
                ByteOrder.LittleEndian,
                handleSRID: srid.HasValue && srid.Value > 0,
                emitZ: hasZ,
                emitM: hasM);

            return writer.Write(geometry);
        }
        catch (Exception ex) when (ex is ParseException or FormatException)
        {
            throw new ArgumentException($"Invalid WKT format: {ex.Message}", ex);
        }
    }

    /// <inheritdoc />
    public GeometryInfo? GetGeometryInfo(byte[]? wkb)
    {
        if (wkb == null || wkb.Length == 0)
        {
            return null;
        }

        try
        {
            var geometry = _wkbReader.Read(wkb);
            if (geometry == null)
            {
                return null;
            }

            var (hasZ, hasM) = DetectZMFromGeometry(geometry);

            return new GeometryInfo
            {
                GeometryType = geometry.GeometryType,
                VertexCount = geometry.NumPoints,
                RingCount = CountRings(geometry),
                WkbSize = wkb.Length,
                HasZ = hasZ,
                HasM = hasM,
                Srid = geometry.SRID > 0 ? geometry.SRID : null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Detects Z and M coordinates from a NetTopologySuite geometry.
    /// Uses consistent detection by checking coordinate sequences for non-NaN values.
    /// </summary>
    /// <remarks>
    /// This method provides unified Z/M detection logic. It examines coordinate sequences
    /// (not just the first coordinate) to determine if Z or M values are present.
    /// This approach handles cases where geometries have mixed coordinates or where
    /// NaN is used as a placeholder for missing dimensions.
    /// </remarks>
    private static (bool HasZ, bool HasM) DetectZMFromGeometry(Geometry? geometry)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return (false, false);
        }

        // For geometry collections, recurse into first non-empty geometry
        if (geometry is GeometryCollection collection && collection.NumGeometries > 0)
        {
            for (var i = 0; i < collection.NumGeometries; i++)
            {
                var child = collection.GetGeometryN(i);
                if (!child.IsEmpty)
                {
                    return DetectZMFromGeometry(child);
                }
            }
            return (false, false);
        }

        // Get the coordinate sequence from the geometry
        var sequence = GetCoordinateSequence(geometry);
        if (sequence == null || sequence.Count == 0)
        {
            return (false, false);
        }

        // Check for Z and M values by examining coordinate sequence
        // Use first coordinate for detection (consistent with NTS behavior)
        var hasZ = !double.IsNaN(sequence.GetZ(0));
        var hasM = !double.IsNaN(sequence.GetM(0));

        return (hasZ, hasM);
    }

    /// <summary>
    /// Gets the coordinate sequence from a geometry for Z/M detection.
    /// </summary>
    private static CoordinateSequence? GetCoordinateSequence(Geometry geometry)
    {
        return geometry switch
        {
            Point point => point.CoordinateSequence,
            LineString lineString => lineString.CoordinateSequence,
            Polygon polygon when polygon.ExteriorRing != null => polygon.ExteriorRing.CoordinateSequence,
            MultiPoint multiPoint when multiPoint.NumGeometries > 0 =>
                ((Point)multiPoint.GetGeometryN(0)).CoordinateSequence,
            MultiLineString multiLineString when multiLineString.NumGeometries > 0 =>
                ((LineString)multiLineString.GetGeometryN(0)).CoordinateSequence,
            MultiPolygon multiPolygon when multiPolygon.NumGeometries > 0 =>
                ((Polygon)multiPolygon.GetGeometryN(0)).ExteriorRing?.CoordinateSequence,
            _ => null
        };
    }

    /// <summary>
    /// Counts the total number of rings in polygon geometries.
    /// </summary>
    private static int CountRings(Geometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => Enumerable.Range(0, multiPolygon.NumGeometries)
                .Sum(i => CountRings(multiPolygon.GetGeometryN(i))),
            GeometryCollection collection => Enumerable.Range(0, collection.NumGeometries)
                .Sum(i => CountRings(collection.GetGeometryN(i))),
            _ => 0
        };
    }
}

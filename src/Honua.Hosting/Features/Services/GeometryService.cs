// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Geometry.Abstractions;
using Microsoft.Extensions.Options;
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
/// - ../Features/Protocols/Ogc/Api/Features/FeaturesEndpoints.cs (GetHasZandM method)
/// - ../Features/Protocols/GeoServices/GeoServicesGeometryConverter.cs (GetHasZandM, HasOrdinateValues methods)
/// - ../Features/Protocols/GeoServices/FeatureServer/Services/GeometryValidator.cs (HasZ/HasM detection in ValidateWkb)
/// </para>
/// </remarks>
internal sealed class GeometryService : IGeometryService
{
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKTReader _wktReader = new();
    private static readonly GeoJsonReader _geoJsonReader = new();
    private static readonly GeoJsonWriter _geoJsonWriter = new();
    private readonly GeometryLimits _geometryLimits;
    private readonly GeometryValidationOptions _validationOptions;

    public GeometryService(IOptions<LimitsOptions> limitsOptions)
    {
        _geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        _validationOptions = limitsOptions?.Value?.Validation ?? new GeometryValidationOptions();
    }

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

            var processed = GeometryOutputProcessor.ApplyLimits(geometry, _geometryLimits);
            return processed == null ? null : _geoJsonWriter.Write(processed);
        }
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException)
        {
            throw new ArgumentException("Invalid WKB geometry format.", ex);
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

        ValidateInputGeometryTextSize(geoJson);

        try
        {
            var geometry = _geoJsonReader.Read<Geometry>(geoJson);
            ValidateInputGeometryComplexity(geometry);
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
        catch (Exception ex) when (ex is ParseException or FormatException or JsonException or Newtonsoft.Json.JsonReaderException)
        {
            throw new ArgumentException("Invalid GeoJSON format.", ex);
        }
    }

    /// <inheritdoc />
    public byte[]? ConvertWktToWkb(string? wkt, int? srid = null)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            return null;
        }

        ValidateInputGeometryTextSize(wkt);

        try
        {
            var geometry = _wktReader.Read(wkt);
            ValidateInputGeometryComplexity(geometry);
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
            throw new ArgumentException("Invalid WKT format.", ex);
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
    internal static (bool HasZ, bool HasM) DetectZMFromGeometry(Geometry? geometry)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return (false, false);
        }
        var hasZ = false;
        var hasM = false;

        foreach (var sequence in EnumerateCoordinateSequences(geometry))
        {
            for (var i = 0; i < sequence.Count; i++)
            {
                if (!hasZ && !double.IsNaN(sequence.GetOrdinate(i, Ordinate.Z)))
                {
                    hasZ = true;
                }

                if (!hasM && !double.IsNaN(sequence.GetOrdinate(i, Ordinate.M)))
                {
                    hasM = true;
                }

                if (hasZ && hasM)
                {
                    return (true, true);
                }
            }
        }

        return (hasZ, hasM);
    }

    private static IEnumerable<CoordinateSequence> EnumerateCoordinateSequences(Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            yield break;
        }

        switch (geometry)
        {
            case Point point:
                yield return point.CoordinateSequence;
                yield break;
            case LineString lineString:
                yield return lineString.CoordinateSequence;
                yield break;
            case Polygon polygon:
                if (polygon.ExteriorRing != null && !polygon.ExteriorRing.IsEmpty)
                {
                    yield return polygon.ExteriorRing.CoordinateSequence;
                }

                for (var i = 0; i < polygon.NumInteriorRings; i++)
                {
                    var interiorRing = polygon.GetInteriorRingN(i);
                    if (interiorRing != null && !interiorRing.IsEmpty)
                    {
                        yield return interiorRing.CoordinateSequence;
                    }
                }

                yield break;
            case GeometryCollection collection:
                for (var i = 0; i < collection.NumGeometries; i++)
                {
                    foreach (var sequence in EnumerateCoordinateSequences(collection.GetGeometryN(i)))
                    {
                        yield return sequence;
                    }
                }
                yield break;
        }
    }

    private void ValidateInputGeometryTextSize(string geometryText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(geometryText);

        var maxInputBytes = ResolveMaxInputGeometryBytes();
        if (maxInputBytes > 0 && Encoding.UTF8.GetByteCount(geometryText) > maxInputBytes)
        {
            throw new ArgumentException("Geometry payload exceeds the configured maximum size.");
        }
    }

    private void ValidateInputGeometryComplexity(Geometry? geometry)
    {
        if (geometry == null || geometry.IsEmpty)
        {
            return;
        }

        var maxVertices = ResolveMaxVertices();
        if (maxVertices > 0 && geometry.NumPoints > maxVertices)
        {
            throw new ArgumentException("Geometry exceeds the configured complexity limits.");
        }

        var maxRings = _validationOptions.MaxRings;
        if (maxRings > 0 && CountRings(geometry) > maxRings)
        {
            throw new ArgumentException("Geometry exceeds the configured complexity limits.");
        }
    }

    private int ResolveMaxInputGeometryBytes()
    {
        var candidates = new List<long>(2);
        if (_geometryLimits.MaxGeometrySize > 0)
        {
            candidates.Add(_geometryLimits.MaxGeometrySize);
        }

        if (_validationOptions.MaxWkbSize > 0)
        {
            candidates.Add(_validationOptions.MaxWkbSize);
        }

        return candidates.Count == 0 ? 0 : (int)candidates.Min();
    }

    private int ResolveMaxVertices()
    {
        var candidates = new List<int>(2);
        if (_geometryLimits.MaxVerticesPerGeometry > 0)
        {
            candidates.Add(_geometryLimits.MaxVerticesPerGeometry);
        }

        if (_validationOptions.MaxVertices > 0)
        {
            candidates.Add(_validationOptions.MaxVertices);
        }

        return candidates.Count == 0 ? 0 : candidates.Min();
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

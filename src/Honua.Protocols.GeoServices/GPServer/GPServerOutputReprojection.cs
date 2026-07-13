// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Geometries;
using Honua.Infrastructure.Rendering;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>
/// Honors the GP environment control <c>env:outSR</c> for synchronous results
/// by reprojecting a <c>data:application/geo+json</c> output artifact from the
/// process's working SRID to the requested output SRID, using the same
/// in-memory <see cref="CoordinateTransformer"/> path the
/// <c>geometry.project</c> executor uses.
///
/// The deterministic <c>geometry.*</c> executors emit a single GeoJSON
/// <c>Feature</c> as a base64 <c>data:</c> URI. When the requested transform is
/// supported, the coordinates are walked and rewritten; the GeoJSON structure
/// and feature properties are preserved. When the transform is not supported
/// (e.g. a datum-shift pair requiring <c>ST_Transform</c>, or a non-vector
/// output), the caller is told to omit <c>env:outSR</c> rather than receiving a
/// silently-unprojected result.
/// </summary>
internal static class GPServerOutputReprojection
{
    private const string GeoJsonDataUriPrefix = "data:application/geo+json;base64,";

    // GeoJsonWriter must not be shared across threads; a thread-static instance reuses one writer
    // per thread instead of allocating one per served FeatureLayer artifact.
    [ThreadStatic]
    private static GeoJsonWriter? _geoJsonWriter;

    private static GeoJsonWriter SharedWriter => _geoJsonWriter ??= new GeoJsonWriter();

    internal readonly record struct ReprojectionOutcome(
        bool Reprojected,
        string? Value,
        string? CapabilityMessage);

    /// <summary>
    /// Returns <c>true</c> when the requested transform from <paramref name="fromSrid"/>
    /// to <paramref name="toSrid"/> is supported by the in-memory transform path.
    /// </summary>
    public static bool IsTransformSupported(int fromSrid, int toSrid)
    {
        if (fromSrid <= 0 || toSrid <= 0)
        {
            return false;
        }

        if (fromSrid == toSrid)
        {
            return true;
        }

        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
        {
            return true;
        }

        if (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && toSrid == 4326)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Attempts to reproject a GeoJSON <c>data:</c> URI output value to
    /// <paramref name="outSrid"/>. Non-GeoJSON values and unsupported transforms
    /// are reported through <see cref="ReprojectionOutcome.CapabilityMessage"/>.
    /// </summary>
    public static ReprojectionOutcome TryReprojectGeoJsonValue(string? value, int fromSrid, int outSrid)
    {
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith(GeoJsonDataUriPrefix, StringComparison.Ordinal))
        {
            return new ReprojectionOutcome(
                Reprojected: false,
                Value: value,
                CapabilityMessage:
                    $"env:outSR={outSrid} could not be applied: this task's output is not a reprojectable " +
                    "GeoJSON geometry. Omit env:outSR or use geometry.project for reprojection.");
        }

        if (fromSrid <= 0)
        {
            return new ReprojectionOutcome(
                Reprojected: false,
                Value: value,
                CapabilityMessage:
                    $"env:outSR={outSrid} could not be applied: the input spatial reference is unknown. " +
                    "Provide 'srid' (or an esriGeometry with a spatialReference) so the output can be reprojected.");
        }

        if (fromSrid == outSrid)
        {
            return new ReprojectionOutcome(Reprojected: true, Value: value, CapabilityMessage: null);
        }

        if (!IsTransformSupported(fromSrid, outSrid))
        {
            return new ReprojectionOutcome(
                Reprojected: false,
                Value: value,
                CapabilityMessage:
                    $"env:outSR={outSrid} is not supported for the in-memory transform path from SRID {fromSrid}. " +
                    "Supported pairs: identity, Web Mercator aliases (3857/900913/102100/102113/3785), and " +
                    "WGS 84 (4326) <-> Web Mercator. Datum-shift pairs requiring ST_Transform are not yet supported.");
        }

        string featureJson;
        try
        {
            var base64 = value.AsSpan(GeoJsonDataUriPrefix.Length);
            featureJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64.ToString()));
        }
        catch (FormatException)
        {
            return new ReprojectionOutcome(
                Reprojected: false,
                Value: value,
                CapabilityMessage: $"env:outSR={outSrid} could not be applied: output payload is not valid base64 GeoJSON.");
        }

        try
        {
            var rewritten = ReprojectFeatureJson(featureJson, fromSrid, outSrid);
            var encoded = GeoJsonDataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(rewritten));
            return new ReprojectionOutcome(Reprojected: true, Value: encoded, CapabilityMessage: null);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return new ReprojectionOutcome(
                Reprojected: false,
                Value: value,
                CapabilityMessage: $"env:outSR={outSrid} could not be applied: {ex.Message}");
        }
    }

    /// <summary>
    /// Normalizes polygon ring winding in a FeatureLayer GeoJSON <c>data:</c> URI value to the
    /// RFC 7946 right-hand rule so the non-reprojected egress path (no <c>env:outSR</c>, or an
    /// identity transform) emits the same winding the reprojected path already produces (#2745).
    /// Handles both single <c>Feature</c> payloads (the deterministic <c>geometry.*</c> executors)
    /// and <c>FeatureCollection</c> payloads (layer/overlay tools emitting through
    /// <c>FeatureCollectionArtifact</c>). Non-GeoJSON values, malformed payloads, and geometries
    /// that are already correctly wound are returned unchanged.
    /// </summary>
    /// <param name="value">The served artifact value (a base64 GeoJSON <c>data:</c> URI, or any other string).</param>
    /// <returns>The value with normalized winding, or the original value when no change applies.</returns>
    public static string? NormalizeGeoJsonWinding(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            !value.StartsWith(GeoJsonDataUriPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        string featureJson;
        try
        {
            var base64 = value.AsSpan(GeoJsonDataUriPrefix.Length);
            featureJson = Encoding.UTF8.GetString(Convert.FromBase64String(base64.ToString()));
        }
        catch (FormatException)
        {
            return value;
        }

        try
        {
            using var doc = JsonDocument.Parse(featureJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return value;
            }

            var geoJsonReader = new GeoJsonReader();
            string rewritten;

            // Check "geometry" before "features" so a Feature carrying a "features" foreign
            // member is still treated as a single Feature.
            if (root.TryGetProperty("geometry", out var geometryElement) &&
                geometryElement.ValueKind == JsonValueKind.Object)
            {
                var geometryJson = TryNormalizeGeometryJson(geoJsonReader, geometryElement);
                if (geometryJson is null)
                {
                    // Already correctly wound (or unparseable); keep the original bytes untouched.
                    return value;
                }

                rewritten = SpliceGeometry(root, geometryJson);
            }
            else if (root.TryGetProperty("features", out var featuresElement) &&
                     featuresElement.ValueKind == JsonValueKind.Array)
            {
                var normalizedGeometryJson = NormalizeFeatureGeometries(geoJsonReader, featuresElement);
                if (normalizedGeometryJson is null)
                {
                    return value;
                }

                rewritten = SpliceFeatureCollection(root, normalizedGeometryJson);
            }
            else
            {
                return value;
            }

            return GeoJsonDataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(rewritten));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return value;
        }
    }

    /// <summary>
    /// Normalizes one GeoJSON geometry element to the right-hand rule. Returns the rewritten
    /// geometry JSON, or <c>null</c> when the geometry is empty, unparseable, or already
    /// correctly wound (so the caller preserves the original bytes).
    /// </summary>
    private static string? TryNormalizeGeometryJson(GeoJsonReader geoJsonReader, JsonElement geometryElement)
    {
        var geometry = geoJsonReader.Read<Geometry>(geometryElement.GetRawText());
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }

        var normalized = RingWindingNormalizer.NormalizeToRightHandRule(geometry);
        return ReferenceEquals(normalized, geometry) ? null : SharedWriter.Write(normalized);
    }

    /// <summary>
    /// Normalizes each <c>features[*].geometry</c> of a FeatureCollection. Returns a per-index
    /// array of replacement geometry JSON (<c>null</c> entries keep the original feature), or
    /// <c>null</c> when no feature needed rewinding.
    /// </summary>
    private static string?[]? NormalizeFeatureGeometries(GeoJsonReader geoJsonReader, JsonElement featuresElement)
    {
        string?[]? normalized = null;
        var index = 0;
        foreach (var feature in featuresElement.EnumerateArray())
        {
            if (feature.ValueKind == JsonValueKind.Object &&
                feature.TryGetProperty("geometry", out var geometryElement) &&
                geometryElement.ValueKind == JsonValueKind.Object)
            {
                var geometryJson = TryNormalizeGeometryJson(geoJsonReader, geometryElement);
                if (geometryJson is not null)
                {
                    normalized ??= new string?[featuresElement.GetArrayLength()];
                    normalized[index] = geometryJson;
                }
            }

            index++;
        }

        return normalized;
    }

    private static string ReprojectFeatureJson(string featureJson, int fromSrid, int toSrid)
    {
        using var doc = JsonDocument.Parse(featureJson);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("geometry", out var geometryElement) ||
            geometryElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("GeoJSON output does not contain a geometry object.");
        }

        var reader = new GeoJsonReader();
        var geometry = reader.Read<Geometry>(geometryElement.GetRawText());
        if (geometry is null || geometry.IsEmpty)
        {
            throw new ArgumentException("GeoJSON output geometry is empty.");
        }

        var reprojected =
            fromSrid == toSrid ||
            (SpatialReferenceExtensions.IsWebMercatorSrid(fromSrid) && SpatialReferenceExtensions.IsWebMercatorSrid(toSrid))
                ? geometry.Copy()
                : ReprojectInMemory(geometry, fromSrid, toSrid);

        reprojected.SRID = toSrid;

        var geometryJson = RingWindingNormalizer.WriteGeoJson(SharedWriter, reprojected);
        return SpliceGeometry(root, geometryJson);
    }

    // Re-emits a GeoJSON Feature object, replacing its "geometry" member with pre-serialized
    // geometry JSON and preserving all other members (id, properties, bbox, foreign members).
    private static string SpliceGeometry(JsonElement root, string geometryJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObjectWithGeometry(writer, root, geometryJson);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Re-emits a GeoJSON FeatureCollection object, replacing the "geometry" member of each
    // feature whose index carries replacement JSON and preserving every other member verbatim.
    private static string SpliceFeatureCollection(JsonElement root, string?[] normalizedGeometryJson)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "features", StringComparison.Ordinal) &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("features");
                    writer.WriteStartArray();
                    var index = 0;
                    foreach (var feature in property.Value.EnumerateArray())
                    {
                        var geometryJson = index < normalizedGeometryJson.Length
                            ? normalizedGeometryJson[index]
                            : null;
                        if (geometryJson is null)
                        {
                            feature.WriteTo(writer);
                        }
                        else
                        {
                            WriteObjectWithGeometry(writer, feature, geometryJson);
                        }

                        index++;
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // Writes one JSON object, replacing its "geometry" member with pre-serialized geometry JSON
    // and preserving all other members (id, properties, bbox, foreign members).
    private static void WriteObjectWithGeometry(Utf8JsonWriter writer, JsonElement obj, string geometryJson)
    {
        writer.WriteStartObject();
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, "geometry", StringComparison.Ordinal))
            {
                writer.WritePropertyName("geometry");
                using var geometryDoc = JsonDocument.Parse(geometryJson);
                geometryDoc.RootElement.WriteTo(writer);
            }
            else
            {
                property.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static Geometry ReprojectInMemory(Geometry source, int fromSrid, int toSrid)
    {
        var editor = new GeometryEditor(source.Factory);
        return editor.Edit(source, new CoordinateOperation(fromSrid, toSrid));
    }

    private sealed class CoordinateOperation(int fromSrid, int toSrid) : GeometryEditor.CoordinateOperation
    {
        public override Coordinate[] Edit(Coordinate[] coordinates, Geometry geometry)
        {
            ArgumentNullException.ThrowIfNull(coordinates);
            var transformed = new Coordinate[coordinates.Length];
            for (var i = 0; i < coordinates.Length; i++)
            {
                var original = coordinates[i];
                var (x, y) = CoordinateTransformer.TransformPoint(original.X, original.Y, fromSrid, toSrid);

                // Copy the source coordinate to preserve its runtime dimension
                // (CoordinateZ / CoordinateM / CoordinateZM) and only overwrite the
                // horizontal ordinates; rebuilding a bare Coordinate would silently
                // drop Z/M through the transformed sequence (#2744).
                var projected = original.Copy();
                projected.X = x;
                projected.Y = y;
                transformed[i] = projected;
            }

            return transformed;
        }
    }
}

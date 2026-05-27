// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Server.Features.Infrastructure.Rendering;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.GeoServices.GPServer;

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

    private static readonly HashSet<int> WebMercatorAliases =
        new() { 3857, 900913, 102100, 102113, 3785 };

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

        if (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (fromSrid == 4326 && WebMercatorAliases.Contains(toSrid))
        {
            return true;
        }

        if (WebMercatorAliases.Contains(fromSrid) && toSrid == 4326)
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
            (WebMercatorAliases.Contains(fromSrid) && WebMercatorAliases.Contains(toSrid))
                ? geometry.Copy()
                : ReprojectInMemory(geometry, fromSrid, toSrid);

        reprojected.SRID = toSrid;

        var geometryJson = new GeoJsonWriter().Write(reprojected);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
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

        return Encoding.UTF8.GetString(buffer.ToArray());
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
                transformed[i] = new Coordinate(x, y);
            }

            return transformed;
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Shared.Models;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Geoprocessing;

/// <summary>
/// Shared ArcGIS GP geometry/FeatureSet input translator. Both GPServer and the
/// MCP Esri-GP profile call this implementation before catalog parameter
/// normalization, so derived SRIDs and multi-feature capability errors agree.
/// </summary>
public static class EsriGpInputTranslation
{
    public static EsriGpInputTranslationResult Translate(IReadOnlyDictionary<string, string> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var translated = new Dictionary<string, string>(inputs.Count, StringComparer.OrdinalIgnoreCase);
        var anyTranslated = false;
        int? inputSpatialReference = null;

        foreach (var (key, value) in inputs)
        {
            if (!LooksLikeObject(value))
            {
                translated[key] = value;
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(value);
            }
            catch (JsonException)
            {
                translated[key] = value;
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    translated[key] = value;
                    continue;
                }

                if (root.TryGetProperty("features", out var features)
                    && features.ValueKind == JsonValueKind.Array)
                {
                    var count = features.GetArrayLength();
                    if (count == 0)
                    {
                        return Failure(
                            translated,
                            $"Input '{key}' is an empty FeatureSet; supply at least one feature with a geometry.",
                            requiresFeatureCollection: false,
                            inputSpatialReference);
                    }
                    if (count > 1)
                    {
                        return Failure(
                            translated,
                            $"Input '{key}' is a FeatureSet carrying {count} features. "
                            + "Honua geometry.* tasks execute on a single geometry; multi-feature execution "
                            + "requires the feature-collection/layer-level execution stream (use a layer-scoped "
                            + "task such as analytics.* / generalization.* / conversion.feature-project, or "
                            + "submit one feature per request).",
                            requiresFeatureCollection: true,
                            inputSpatialReference);
                    }

                    var feature = features[0];
                    if (feature.ValueKind != JsonValueKind.Object
                        || !feature.TryGetProperty("geometry", out var geometry)
                        || geometry.ValueKind != JsonValueKind.Object)
                    {
                        return Failure(
                            translated,
                            $"Input '{key}' FeatureSet feature is missing a 'geometry' object.",
                            requiresFeatureCollection: false,
                            inputSpatialReference);
                    }

                    var parentSrid = ReadSpatialReference(root);
                    if (!TryConvertGeometry(geometry, parentSrid, out var wkb, out var featureSrid, out var error))
                    {
                        return Failure(
                            translated,
                            $"Input '{key}' FeatureSet geometry could not be translated: {error}",
                            requiresFeatureCollection: false,
                            inputSpatialReference);
                    }
                    translated[key] = wkb;
                    inputSpatialReference ??= featureSrid;
                    anyTranslated = true;
                    continue;
                }

                if (!IsEsriGeometry(root))
                {
                    translated[key] = value;
                    continue;
                }

                if (!TryConvertGeometry(root, null, out var geometryWkb, out var geometrySrid, out var geometryError))
                {
                    return Failure(
                        translated,
                        $"Input '{key}' esriGeometry could not be translated: {geometryError}",
                        requiresFeatureCollection: false,
                        inputSpatialReference);
                }
                translated[key] = geometryWkb;
                inputSpatialReference ??= geometrySrid;
                anyTranslated = true;
            }
        }

        if (anyTranslated && inputSpatialReference is { } derivedSrid && !translated.ContainsKey("srid"))
        {
            translated["srid"] = derivedSrid.ToString(CultureInfo.InvariantCulture);
        }

        return new EsriGpInputTranslationResult(
            translated,
            RequiresFeatureCollectionExecution: false,
            CapabilityMessage: null,
            InputSpatialReference: inputSpatialReference,
            Translated: anyTranslated);
    }

    private static EsriGpInputTranslationResult Failure(
        Dictionary<string, string> translated,
        string message,
        bool requiresFeatureCollection,
        int? srid)
        => new(translated, requiresFeatureCollection, message, srid, Translated: false);

    private static bool TryConvertGeometry(
        JsonElement value,
        int? parentSrid,
        out string wkbBase64,
        out int? spatialReference,
        out string? error)
    {
        wkbBase64 = string.Empty;
        spatialReference = ReadSpatialReference(value) ?? parentSrid;
        error = null;
        try
        {
            var factory = NetTopologySuite.NtsGeometryServices.Instance
                .CreateGeometryFactory(spatialReference ?? 0);
            var geometry = CreateGeometry(value, factory);
            if (geometry == null || geometry.IsEmpty)
            {
                error = "Geometry is empty or has an unsupported shape.";
                return false;
            }

            if (spatialReference is int geometrySrid
                && geometrySrid > 0
                && SpatialReference.Create(geometrySrid).IsGeographic)
            {
                var invalid = geometry.Coordinates.FirstOrDefault(coordinate =>
                    coordinate.X is < -180 or > 180 || coordinate.Y is < -90 or > 90);
                if (invalid != null)
                {
                    error = $"Coordinate ({invalid.X.ToString("G17", CultureInfo.InvariantCulture)}, "
                        + $"{invalid.Y.ToString("G17", CultureInfo.InvariantCulture)}) is outside "
                        + $"the valid range for geographic CRS EPSG:{geometrySrid}.";
                    return false;
                }
            }

            geometry.SRID = spatialReference ?? 0;
            var hasZ = geometry.Coordinates.Any(coordinate => !double.IsNaN(coordinate.Z));
            var hasM = geometry.Coordinates.Any(coordinate => !double.IsNaN(coordinate.M));
            var writer = new WKBWriter(
                ByteOrder.LittleEndian,
                handleSRID: spatialReference is > 0,
                emitZ: hasZ,
                emitM: hasM);
            wkbBase64 = Convert.ToBase64String(writer.Write(geometry));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException)
        {
            error = "Geometry could not be converted.";
            return false;
        }
    }

    private static Geometry? CreateGeometry(JsonElement value, GeometryFactory factory)
    {
        if (TryReadNumber(value, "xmin", out var xmin)
            && TryReadNumber(value, "ymin", out var ymin)
            && TryReadNumber(value, "xmax", out var xmax)
            && TryReadNumber(value, "ymax", out var ymax))
        {
            if (factory.SRID > 0 && SpatialReference.Create(factory.SRID).IsGeographic && xmin > xmax)
            {
                return factory.CreateMultiPolygon([
                    CreateEnvelope(factory, xmin, ymin, 180, ymax),
                    CreateEnvelope(factory, -180, ymin, xmax, ymax)]);
            }
            return factory.ToGeometry(new Envelope(xmin, xmax, ymin, ymax));
        }

        if (TryReadNumber(value, "x", out var x) && TryReadNumber(value, "y", out var y))
        {
            var ordinates = new List<double> { x, y };
            if (TryReadNumber(value, "z", out var z))
            {
                ordinates.Add(z);
            }
            if (TryReadNumber(value, "m", out var m))
            {
                ordinates.Add(m);
            }
            return factory.CreatePoint(CreateCoordinate(
                ordinates,
                ReadFlag(value, "hasZ") || ordinates.Count >= 3,
                ReadFlag(value, "hasM") || ordinates.Count >= 4));
        }

        if (value.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            var geometries = ReadCoordinateArrays(points, value)
                .Select(factory.CreatePoint)
                .ToArray();
            return geometries.Length == 0 ? null : factory.CreateMultiPoint(geometries);
        }

        if (value.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            var lines = paths.EnumerateArray()
                .Where(path => path.ValueKind == JsonValueKind.Array)
                .Select(path => ReadCoordinateArrays(path, value))
                .Where(coordinates => coordinates.Length >= 2)
                .Select(factory.CreateLineString)
                .ToArray();
            return lines.Length switch
            {
                0 => null,
                1 => lines[0],
                _ => factory.CreateMultiLineString(lines)
            };
        }

        if (value.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
        {
            return CreatePolygonalGeometry(rings, value, factory);
        }

        return null;
    }

    private static Geometry? CreatePolygonalGeometry(
        JsonElement rings,
        JsonElement parent,
        GeometryFactory factory)
    {
        var shells = new List<LinearRing>();
        var holes = new List<LinearRing>();
        foreach (var ring in rings.EnumerateArray().Where(ring => ring.ValueKind == JsonValueKind.Array))
        {
            var coordinates = ReadCoordinateArrays(ring, parent);
            if (coordinates.Length < 3)
            {
                continue;
            }
            coordinates = EnsureClosed(coordinates);
            if (coordinates.Length < 4)
            {
                continue;
            }
            var linearRing = factory.CreateLinearRing(coordinates);
            (Orientation.IsCCW(coordinates) ? holes : shells).Add(linearRing);
        }

        if (shells.Count == 0)
        {
            shells.AddRange(holes);
            holes.Clear();
        }
        if (shells.Count == 0)
        {
            return null;
        }

        var assigned = shells.ToDictionary(shell => shell, _ => new List<LinearRing>());
        foreach (var hole in holes)
        {
            var point = factory.CreatePoint(hole.Coordinate);
            var shell = shells.FirstOrDefault(candidate => factory.CreatePolygon(candidate).Covers(point));
            if (shell == null)
            {
                shells.Add(hole);
                assigned[hole] = [];
            }
            else
            {
                assigned[shell].Add(hole);
            }
        }

        var polygons = shells
            .Select(shell => factory.CreatePolygon(shell, assigned[shell].ToArray()))
            .ToArray();
        return polygons.Length == 1 ? polygons[0] : factory.CreateMultiPolygon(polygons);
    }

    private static Coordinate[] ReadCoordinateArrays(JsonElement array, JsonElement parent)
    {
        var hasZ = ReadFlag(parent, "hasZ");
        var hasM = ReadFlag(parent, "hasM");
        var coordinates = new List<Coordinate>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                continue;
            }
            var values = element.EnumerateArray()
                .Where(ordinate => ordinate.ValueKind == JsonValueKind.Number)
                .Select(ordinate => ordinate.GetDouble())
                .ToArray();
            if (values.Length >= 2)
            {
                coordinates.Add(CreateCoordinate(values, hasZ, hasM));
            }
        }
        return coordinates.ToArray();
    }

    private static Coordinate CreateCoordinate(
        IReadOnlyList<double> ordinates,
        bool hasZ,
        bool hasM)
    {
        if (hasZ && hasM && ordinates.Count >= 4)
        {
            return new CoordinateZM(ordinates[0], ordinates[1], ordinates[2], ordinates[3]);
        }
        if (hasM && !hasZ && ordinates.Count >= 3)
        {
            return new CoordinateM(ordinates[0], ordinates[1], ordinates[2]);
        }
        if ((hasZ || ordinates.Count >= 3) && ordinates.Count >= 3)
        {
            return new CoordinateZ(ordinates[0], ordinates[1], ordinates[2]);
        }
        return new Coordinate(ordinates[0], ordinates[1]);
    }

    private static Coordinate[] EnsureClosed(Coordinate[] coordinates)
    {
        if (coordinates[0].Equals2D(coordinates[^1]))
        {
            return coordinates;
        }
        return [.. coordinates, coordinates[0].Copy()];
    }

    private static Polygon CreateEnvelope(
        GeometryFactory factory,
        double xmin,
        double ymin,
        double xmax,
        double ymax)
        => factory.CreatePolygon([
            new Coordinate(xmin, ymin),
            new Coordinate(xmax, ymin),
            new Coordinate(xmax, ymax),
            new Coordinate(xmin, ymax),
            new Coordinate(xmin, ymin)]);

    private static bool IsEsriGeometry(JsonElement value)
        => (value.TryGetProperty("x", out _) && value.TryGetProperty("y", out _))
            || (value.TryGetProperty("xmin", out _) && value.TryGetProperty("ymin", out _)
                && value.TryGetProperty("xmax", out _) && value.TryGetProperty("ymax", out _))
            || value.TryGetProperty("points", out _)
            || value.TryGetProperty("paths", out _)
            || value.TryGetProperty("rings", out _);

    private static int? ReadSpatialReference(JsonElement value)
    {
        if (!value.TryGetProperty("spatialReference", out var spatialReference)
            || spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        if (spatialReference.TryGetProperty("wkid", out var wkid) && wkid.TryGetInt32(out var valueWkid))
        {
            return valueWkid;
        }
        return spatialReference.TryGetProperty("latestWkid", out var latestWkid)
            && latestWkid.TryGetInt32(out var valueLatestWkid)
            ? valueLatestWkid
            : null;
    }

    private static bool TryReadNumber(JsonElement value, string propertyName, out double number)
    {
        number = 0;
        return value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out number);
    }

    private static bool ReadFlag(JsonElement value, string propertyName)
        => value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.True;

    private static bool LooksLikeObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        return value.AsSpan().TrimStart()[0] == '{';
    }
}

public readonly record struct EsriGpInputTranslationResult(
    Dictionary<string, string> Inputs,
    bool RequiresFeatureCollectionExecution,
    string? CapabilityMessage,
    int? InputSpatialReference,
    bool Translated);

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Parses the ImageServer <c>computeClassStatistics</c> <c>classDescriptions</c> document into
/// the training AOIs the class-statistics pipeline consumes. Each class carries an identity plus a
/// training geometry (an Esri polygon or envelope, or an array of them unioned into one AOI),
/// which is converted to a WKB clip geometry for the shared raster analytics path.
/// </summary>
internal static class ImageServerClassDescriptions
{
    private static readonly WKBWriter WkbWriter = new();

    /// <summary>A parsed class description: identity plus its training AOI clip geometry (WKB).</summary>
    internal readonly record struct ParsedClass(int ClassId, string? Name, byte[] ClipGeometry, int? ClipSrid);

    /// <summary>
    /// Parses the <c>classDescriptions</c> JSON (already shape-validated to a
    /// <c>{ "classes": [ ... ] }</c> object). Each class must supply a polygon/envelope training
    /// geometry via <c>geometry</c> or a <c>geometries</c> array. The class id is read from
    /// <c>classId</c>/<c>classValue</c>/<c>id</c> (falling back to the 1-based ordinal). The
    /// optional per-geometry <c>spatialReference.wkid</c> wins over <paramref name="defaultSrid"/>.
    /// </summary>
    internal static bool TryParse(
        string classDescriptionsJson,
        int? defaultSrid,
        out IReadOnlyList<ParsedClass> classes,
        out string? error)
    {
        classes = Array.Empty<ParsedClass>();
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(classDescriptionsJson);
        }
        catch (JsonException)
        {
            error = "classDescriptions must be valid JSON.";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("classes", out var classesElement) ||
                classesElement.ValueKind != JsonValueKind.Array)
            {
                error = "classDescriptions must be a JSON object containing a classes array.";
                return false;
            }

            if (classesElement.GetArrayLength() == 0)
            {
                error = "classDescriptions.classes must contain at least one class.";
                return false;
            }

            var parsed = new List<ParsedClass>(classesElement.GetArrayLength());
            var ordinal = 0;
            foreach (var classElement in classesElement.EnumerateArray())
            {
                ordinal++;
                if (classElement.ValueKind != JsonValueKind.Object)
                {
                    error = "Each class in classDescriptions must be a JSON object.";
                    return false;
                }

                var classId = ReadClassId(classElement, ordinal);
                var name = ReadName(classElement);

                if (!TryBuildClipGeometry(classElement, defaultSrid, out var wkb, out var srid, out var geometryError))
                {
                    error = $"Class {classId}: {geometryError}";
                    return false;
                }

                parsed.Add(new ParsedClass(classId, name, wkb, srid));
            }

            classes = parsed;
            return true;
        }
    }

    private static int ReadClassId(JsonElement classElement, int ordinal)
    {
        foreach (var name in new[] { "classId", "classValue", "id", "value" })
        {
            if (classElement.TryGetProperty(name, out var element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var id))
            {
                return id;
            }
        }

        return ordinal;
    }

    private static string? ReadName(JsonElement classElement)
    {
        foreach (var name in new[] { "name", "classname", "className" })
        {
            if (classElement.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
            {
                return element.GetString();
            }
        }

        return null;
    }

    private static bool TryBuildClipGeometry(
        JsonElement classElement,
        int? defaultSrid,
        out byte[] wkb,
        out int? srid,
        out string? error)
    {
        wkb = Array.Empty<byte>();
        srid = null;
        error = null;

        var geometryElements = new List<JsonElement>();
        if (classElement.TryGetProperty("geometry", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            geometryElements.Add(single);
        }

        if (classElement.TryGetProperty("geometries", out var many) && many.ValueKind == JsonValueKind.Array)
        {
            geometryElements.AddRange(many.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.Object));
        }

        if (geometryElements.Count == 0)
        {
            error = "a training geometry is required (supply 'geometry' or 'geometries').";
            return false;
        }

        var geometries = new List<Geometry>(geometryElements.Count);
        int? resolvedSrid = null;
        foreach (var element in geometryElements)
        {
            if (!TryReadGeometry(element, out var geometry, out var geometryError))
            {
                error = geometryError;
                return false;
            }

            resolvedSrid ??= ReadWkid(element);
            geometries.Add(geometry);
        }

        var clip = geometries.Count == 1 ? geometries[0] : UnaryUnionOp.Union(geometries);
        srid = resolvedSrid ?? defaultSrid;
        wkb = WkbWriter.Write(clip);
        return true;
    }

    private static bool TryReadGeometry(JsonElement geometry, out Geometry result, out string? error)
    {
        result = null!;
        error = null;
        var factory = new GeometryFactory();

        // Envelope form.
        if (TryReadNumber(geometry, "xmin", out var xmin) &&
            TryReadNumber(geometry, "ymin", out var ymin) &&
            TryReadNumber(geometry, "xmax", out var xmax) &&
            TryReadNumber(geometry, "ymax", out var ymax))
        {
            var envelope = new Envelope(Math.Min(xmin, xmax), Math.Max(xmin, xmax), Math.Min(ymin, ymax), Math.Max(ymin, ymax));
            result = factory.ToGeometry(envelope);
            return true;
        }

        // Polygon form (rings). The first ring is the exterior shell; subsequent rings are treated
        // as holes (the common single-part Esri polygon). Multi-part exterior rings beyond the
        // first are not decomposed — a documented limitation for training AOIs.
        if (geometry.TryGetProperty("rings", out var ringsElement) && ringsElement.ValueKind == JsonValueKind.Array)
        {
            var rings = new List<LinearRing>();
            foreach (var ringElement in ringsElement.EnumerateArray())
            {
                if (!TryReadRing(ringElement, factory, out var ring, out var ringError))
                {
                    error = ringError;
                    return false;
                }

                rings.Add(ring);
            }

            if (rings.Count == 0)
            {
                error = "polygon geometry must contain at least one ring.";
                return false;
            }

            var holes = rings.Count > 1 ? rings.Skip(1).ToArray() : null;
            result = factory.CreatePolygon(rings[0], holes);
            return true;
        }

        error = "training geometry must be an Esri polygon (rings) or envelope (xmin/ymin/xmax/ymax).";
        return false;
    }

    private static bool TryReadRing(JsonElement ringElement, GeometryFactory factory, out LinearRing ring, out string? error)
    {
        ring = null!;
        error = null;

        if (ringElement.ValueKind != JsonValueKind.Array)
        {
            error = "each polygon ring must be an array of coordinate pairs.";
            return false;
        }

        var coordinates = new List<Coordinate>(ringElement.GetArrayLength());
        foreach (var pair in ringElement.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2 ||
                pair[0].ValueKind != JsonValueKind.Number || pair[1].ValueKind != JsonValueKind.Number)
            {
                error = "each polygon vertex must be a [x, y] number pair.";
                return false;
            }

            coordinates.Add(new Coordinate(pair[0].GetDouble(), pair[1].GetDouble()));
        }

        // Esri rings are closed; defensively close an open ring so NTS accepts it.
        if (coordinates.Count >= 3 && !coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0].Copy());
        }

        if (coordinates.Count < 4)
        {
            error = "each polygon ring must have at least three distinct vertices.";
            return false;
        }

        try
        {
            ring = factory.CreateLinearRing(coordinates.ToArray());
            return true;
        }
        catch (ArgumentException)
        {
            error = "polygon ring is not a valid closed ring.";
            return false;
        }
    }

    private static bool TryReadNumber(JsonElement element, string property, out double value)
    {
        value = 0;
        if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.TryGetDouble(out value);
        }

        return false;
    }

    private static int? ReadWkid(JsonElement geometry)
    {
        if (!geometry.TryGetProperty("spatialReference", out var sr) || sr.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (sr.TryGetProperty("latestWkid", out var latest) &&
            latest.ValueKind == JsonValueKind.Number && latest.TryGetInt32(out var latestWkid))
        {
            return latestWkid;
        }

        if (sr.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number && wkid.TryGetInt32(out var wkidValue))
        {
            return wkidValue;
        }

        return null;
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NetTopologySuite.Operation.Union;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerClassDescriptions
{
    private static readonly WKBWriter WkbWriter = new();
    private static readonly string[] ClassIdPropertyNames = ["classId", "classValue", "id", "value"];
    private static readonly string[] ClassNamePropertyNames = ["name", "classname", "className"];

    internal readonly record struct ParsedClass(int ClassId, string? Name, byte[] ClipGeometry, int? ClipSrid);

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
        var propertyIndex = 0;
        while (propertyIndex < ClassIdPropertyNames.Length)
        {
            if (classElement.TryGetProperty(ClassIdPropertyNames[propertyIndex], out var element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var id))
            {
                return id;
            }

            propertyIndex++;
        }

        return ordinal;
    }

    private static string? ReadName(JsonElement classElement)
    {
        var propertyIndex = 0;
        while (propertyIndex < ClassNamePropertyNames.Length)
        {
            if (classElement.TryGetProperty(ClassNamePropertyNames[propertyIndex], out var element) &&
                element.ValueKind == JsonValueKind.String &&
                element.GetString() is { } value)
            {
                return value;
            }

            propertyIndex++;
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
            if (!ImageServerEsriPolygonReader.TryRead(element, "training geometry", out var geometry, out var geometryError))
            {
                error = geometryError;
                return false;
            }

            resolvedSrid ??= ImageServerGeometryHelpers.ReadWkid(element);
            geometries.Add(geometry);
        }

        var clip = geometries.Count == 1 ? geometries[0] : UnaryUnionOp.Union(geometries);
        srid = resolvedSrid ?? defaultSrid;
        wkb = WkbWriter.Write(clip);
        return true;
    }
}

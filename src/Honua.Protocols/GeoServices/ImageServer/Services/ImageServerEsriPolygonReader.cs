// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using NetTopologySuite.Geometries;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// Reads an Esri JSON envelope or single-part polygon for the ImageServer operations that take
// an area of interest. subject names the geometry in the error message.
internal static class ImageServerEsriPolygonReader
{
    internal static bool TryRead(JsonElement geometry, string subject, out Geometry result, out string? error)
    {
        result = null!;
        error = null;
        var factory = new GeometryFactory();

        // Envelope form.
        if (ImageServerGeometryHelpers.TryReadNumber(geometry, "xmin", out var xmin) &&
            ImageServerGeometryHelpers.TryReadNumber(geometry, "ymin", out var ymin) &&
            ImageServerGeometryHelpers.TryReadNumber(geometry, "xmax", out var xmax) &&
            ImageServerGeometryHelpers.TryReadNumber(geometry, "ymax", out var ymax))
        {
            var envelope = new Envelope(Math.Min(xmin, xmax), Math.Max(xmin, xmax), Math.Min(ymin, ymax), Math.Max(ymin, ymax));
            result = factory.ToGeometry(envelope);
            return true;
        }

        // Polygon form (rings). The first ring is the exterior shell; subsequent rings are treated
        // as holes (the common single-part Esri polygon). Multi-part exterior rings beyond the
        // first are not decomposed.
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

        error = $"{subject} must be an Esri polygon (rings) or envelope (xmin/ymin/xmax/ymax).";
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
}

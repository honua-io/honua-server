// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Validation;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Admits query geometry through the existing GeoServices parser and enforces
/// a vertex budget before the query handler performs CRS and spatial validation.
/// </summary>
internal sealed class GeoServicesQueryGeometryMetadata : GeometryParameterMetadata
{
    internal static GeoServicesQueryGeometryMetadata Instance { get; } = new();

    public override string ParameterName => "geometry";

    public override string? Validate(string value, int maxVertices)
    {
        if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(value, null, out var geometry, out var error))
        {
            return error ?? "Invalid geometry parameter.";
        }

        if (geometry == null)
        {
            return null;
        }

        var vertexCount = 0;
        if (geometry.X.HasValue || geometry.Y.HasValue)
        {
            if (!IsFinite(geometry.X) || !IsFinite(geometry.Y) ||
                (geometry.Z.HasValue && !IsFinite(geometry.Z)) ||
                (geometry.M.HasValue && !IsFinite(geometry.M)))
            {
                return "Geometry point must contain finite X/Y and any supplied Z/M ordinates.";
            }
            vertexCount++;
        }
        if (geometry.Xmin.HasValue || geometry.Ymin.HasValue || geometry.Xmax.HasValue || geometry.Ymax.HasValue)
        {
            if (!IsFinite(geometry.Xmin) || !IsFinite(geometry.Ymin) ||
                !IsFinite(geometry.Xmax) || !IsFinite(geometry.Ymax))
            {
                return "Geometry envelope must contain four finite bounds.";
            }
            vertexCount += 2;
        }

        if (geometry.Points != null && !CountVertices(geometry.Points, ref vertexCount, maxVertices))
        {
            return VertexError(maxVertices);
        }
        foreach (var parts in new[] { geometry.Paths, geometry.Rings })
        {
            if (parts == null)
            {
                continue;
            }
            foreach (var part in parts)
            {
                if (part == null || !CountVertices(part, ref vertexCount, maxVertices))
                {
                    return VertexError(maxVertices);
                }
            }
        }

        return vertexCount == 0 || vertexCount > maxVertices ? VertexError(maxVertices) : null;
    }

    private static bool IsFinite(double? value) => value.HasValue && double.IsFinite(value.Value);

    private static bool CountVertices(double[][] positions, ref int count, int maxVertices)
    {
        foreach (var position in positions)
        {
            if (position == null || position.Length is < 2 or > 4 || ++count > maxVertices)
            {
                return false;
            }
            foreach (var coordinate in position)
            {
                if (!double.IsFinite(coordinate))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static string VertexError(int maxVertices) =>
        $"Geometry must contain valid coordinate positions and at most {maxVertices} vertices (Limits:Geometry:MaxVerticesPerGeometry).";
}

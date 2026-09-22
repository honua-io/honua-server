// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Geometries;
using Honua.Infrastructure.Validation;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Admits query geometry through the existing GeoServices parser and enforces
/// a vertex budget before the query handler performs CRS and spatial validation.
/// </summary>
internal sealed class GeoServicesQueryGeometryMetadata : GeometryParameterMetadata
{
    internal static GeoServicesQueryGeometryMetadata Instance { get; } = new();

    public override string ParameterName => "geometry";

    public override async Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> ReadBodyParametersAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(request.Method) ||
            !GeoServicesRequestValueHelpers.TryValidateRequestContentType(request, out _))
        {
            // Preserve the handler's existing unsupported-content-type response.
            return (null, null);
        }

        request.EnableBuffering();
        var position = request.Body.Position;
        try
        {
            return await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(request, cancellationToken);
        }
        catch (InvalidDataException)
        {
            return (null, "Invalid query form payload.");
        }
        finally
        {
            request.Body.Position = position;
        }
    }

    public override string? Validate(string value, int maxVertices, CancellationToken cancellationToken = default)
    {
        if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(value, null, out var geometry, out var error))
        {
            return error ?? "Invalid geometry parameter.";
        }

        if (geometry == null)
        {
            return null;
        }

        if (GeoServicesGeometryConverter.HasTrueCurves(geometry))
        {
            try
            {
                var curveParts = geometry.CurvePaths ?? geometry.CurveRings;
                var curveVertexCount = 0;
                foreach (var part in curveParts ?? [])
                {
                    if (part == null)
                    {
                        return VertexError(maxVertices);
                    }

                    curveVertexCount += CurveGeometryConverter.CountDensifiedVertices(
                        part,
                        maxVertices - curveVertexCount,
                        cancellationToken);
                }

                return curveVertexCount == 0 || curveVertexCount > maxVertices
                    ? VertexError(maxVertices)
                    : null;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException or OverflowException)
            {
                return $"{exception.Message} (Limits:Geometry:MaxVerticesPerGeometry: {maxVertices}).";
            }
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

            if (!double.IsFinite(position[0]) || !double.IsFinite(position[1]))
            {
                return false;
            }

            for (var ordinate = 2; ordinate < position.Length; ordinate++)
            {
                var coordinate = position[ordinate];
                if (!double.IsFinite(coordinate) && !double.IsNaN(coordinate))
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

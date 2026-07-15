// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text.Json;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.FileImport.Services;

/// <summary>
/// Reader for Esri JSON feature sets (the ArcGIS <c>FeatureSet</c> shape). Each feature carries an
/// <c>attributes</c> object and an Esri geometry (<c>x</c>/<c>y</c>, <c>points</c>, <c>paths</c>, or
/// <c>rings</c>). This lets Esri-shaped payloads import correctly instead of being silently
/// misinterpreted as GeoJSON (honua-server#2352).
/// </summary>
internal static class EsriJsonFormatReader
{
    /// <summary>
    /// Streams features parsed from an Esri JSON feature set.
    /// </summary>
    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var srid = TryReadWkid(root) ?? 4326;
        var factory = new GeometryFactory(new PrecisionModel(), srid);

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("features", out var features) ||
            features.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        var recordIndex = 0;
        foreach (var element in features.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            NtsGeometry? geometry = null;
            if (element.TryGetProperty("geometry", out var geometryElement) &&
                geometryElement.ValueKind == JsonValueKind.Object)
            {
                geometry = ParseGeometry(geometryElement, factory);
            }

            var attributes = new AttributesTable();
            if (element.TryGetProperty("attributes", out var attributesElement) &&
                attributesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var attribute in attributesElement.EnumerateObject().Where(a => !attributes.Exists(a.Name)))
                {
                    attributes.Add(attribute.Name, GetAttributeValue(attribute.Value));
                }
            }

            yield return new Feature(geometry, attributes);

            if (++recordIndex % 256 == 0)
            {
                await Task.Yield();
            }
        }
    }

    /// <summary>
    /// Reads the feature set's spatial reference well-known id (<c>spatialReference.wkid</c> or
    /// <c>latestWkid</c>) so the import pipeline can assign the source CRS. Returns
    /// <see langword="null"/> when the document has no usable spatial reference.
    /// </summary>
    internal static async Task<int?> TryDetectSridAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return TryReadWkid(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryReadWkid(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("spatialReference", out var spatialReference) ||
            spatialReference.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (spatialReference.TryGetProperty("latestWkid", out var latestWkid) &&
            latestWkid.ValueKind == JsonValueKind.Number &&
            latestWkid.TryGetInt32(out var latest))
        {
            return NormalizeWkid(latest);
        }

        if (spatialReference.TryGetProperty("wkid", out var wkid) &&
            wkid.ValueKind == JsonValueKind.Number &&
            wkid.TryGetInt32(out var value))
        {
            return NormalizeWkid(value);
        }

        return null;
    }

    /// <summary>
    /// Maps Esri-only well-known ids that have no matching row in PostGIS <c>spatial_ref_sys</c> to
    /// their EPSG equivalents so the source CRS validates. ArcGIS commonly emits <c>wkid</c> alone
    /// (no <c>latestWkid</c>) with the legacy Web Mercator codes 102100 / 102113 / 900913, all of
    /// which are EPSG:3857; passing them through verbatim would fail import SRID validation. Codes
    /// that already correspond to a registered EPSG entry are returned unchanged.
    /// </summary>
    private static int NormalizeWkid(int wkid) => wkid switch
    {
        102100 or 102113 or 900913 => 3857,
        _ => wkid,
    };

    private static NtsGeometry? ParseGeometry(JsonElement geometry, GeometryFactory factory)
    {
        // Point: {"x": .., "y": ..}
        if (geometry.TryGetProperty("x", out var x) && geometry.TryGetProperty("y", out var y) &&
            TryGetDouble(x, out var px) && TryGetDouble(y, out var py))
        {
            return factory.CreatePoint(new Coordinate(px, py));
        }

        // Multipoint: {"points": [[x, y], ...]}
        if (geometry.TryGetProperty("points", out var points) && points.ValueKind == JsonValueKind.Array)
        {
            var coordinates = ReadCoordinates(points);
            return coordinates.Length == 0 ? null : factory.CreateMultiPointFromCoords(coordinates);
        }

        // Polyline: {"paths": [[[x, y], ...], ...]}
        if (geometry.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            var lines = ReadPartsAsLineStrings(paths, factory);
            return lines.Length switch
            {
                0 => null,
                1 => lines[0],
                _ => factory.CreateMultiLineString(lines)
            };
        }

        // Polygon: {"rings": [[[x, y], ...], ...]}
        if (geometry.TryGetProperty("rings", out var rings) && rings.ValueKind == JsonValueKind.Array)
        {
            return ParsePolygon(rings, factory);
        }

        return null;
    }

    private static NtsGeometry? ParsePolygon(JsonElement rings, GeometryFactory factory)
    {
        // Esri polygons list exterior rings clockwise and interior rings (holes) counter-clockwise,
        // with holes following their containing exterior ring. Group accordingly into one or more
        // NTS polygons.
        var polygons = new List<Polygon>();
        LinearRing? shell = null;
        var holes = new List<LinearRing>();

        void FlushShell()
        {
            if (shell != null)
            {
                polygons.Add(factory.CreatePolygon(shell, holes.ToArray()));
                holes.Clear();
                shell = null;
            }
        }

        // Not a simple map/select: this loop folds rings into shell/hole state across iterations
        // (via FlushShell), so an imperative loop is clearer than a LINQ chain here.
        foreach (var ringElement in rings.EnumerateArray())
        {
            var coordinates = ReadCoordinates(ringElement);
            if (coordinates.Length < 4)
            {
                continue;
            }

            var closed = EnsureClosed(coordinates);
            var ring = factory.CreateLinearRing(closed);

            // Signed area > 0 is counter-clockwise (an Esri hole); <= 0 is clockwise (an exterior).
            if (Area.OfRingSigned(closed) > 0 && shell != null)
            {
                holes.Add(ring);
            }
            else
            {
                FlushShell();
                shell = ring;
            }
        }

        FlushShell();

        return polygons.Count switch
        {
            0 => null,
            1 => polygons[0],
            _ => factory.CreateMultiPolygon(polygons.ToArray())
        };
    }

    private static LineString[] ReadPartsAsLineStrings(JsonElement parts, GeometryFactory factory) =>
        parts.EnumerateArray()
            .Select(ReadCoordinates)
            .Where(coordinates => coordinates.Length >= 2)
            .Select(factory.CreateLineString)
            .ToArray();

    private static Coordinate[] ReadCoordinates(JsonElement coordinateArray)
    {
        var coordinates = new List<Coordinate>();
        foreach (var pair in coordinateArray.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var ordinates = pair.EnumerateArray();
            if (!ordinates.MoveNext() || !TryGetDouble(ordinates.Current, out var cx))
            {
                continue;
            }

            if (!ordinates.MoveNext() || !TryGetDouble(ordinates.Current, out var cy))
            {
                continue;
            }

            coordinates.Add(new Coordinate(cx, cy));
        }

        return coordinates.ToArray();
    }

    private static Coordinate[] EnsureClosed(Coordinate[] coordinates)
    {
        if (coordinates.Length > 0 && !coordinates[0].Equals2D(coordinates[^1]))
        {
            var closed = new Coordinate[coordinates.Length + 1];
            Array.Copy(coordinates, closed, coordinates.Length);
            closed[^1] = coordinates[0].Copy();
            return closed;
        }

        return coordinates;
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static object? GetAttributeValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
    }
}

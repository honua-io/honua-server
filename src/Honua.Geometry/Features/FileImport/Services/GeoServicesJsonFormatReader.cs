// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Import.Domain;
using System.Text.Json;
using Honua.Core.Geometries;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.FileImport.Services;

internal static class GeoServicesJsonFormatReader
{
    internal static IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        CancellationToken cancellationToken)
        => ReadStreamingAsync(stream, ImportLimits.Default, cancellationToken);

    internal static async IAsyncEnumerable<IFeature> ReadStreamingAsync(
        Stream stream,
        ImportLimits? limits,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var effectiveLimits = limits ?? ImportLimits.Default;
        var maxMemoryBytes = effectiveLimits.MaxMemoryBytes > 0
            ? effectiveLimits.MaxMemoryBytes
            : ImportLimits.Default.MaxMemoryBytes;
        var maxVertices = effectiveLimits.MaxVertices > 0
            ? effectiveLimits.MaxVertices
            : ImportLimits.Default.MaxVertices;
        var maxRings = effectiveLimits.MaxRings > 0
            ? effectiveLimits.MaxRings
            : ImportLimits.Default.MaxRings;

        if (stream.CanSeek && stream.Length - stream.Position > maxMemoryBytes)
        {
            throw new InvalidDataException(
                $"GeoServices JSON payload exceeds maximum size of {maxMemoryBytes:N0} bytes.");
        }

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
                geometry = ParseGeometry(geometryElement, factory, maxVertices, maxRings);
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

    internal static async Task<int?> TryDetectSridAsync(Stream stream, CancellationToken cancellationToken)
        => (await InspectCoordinateSystemAsync(stream, cancellationToken)).DetectedSrid;

    internal static async Task<ImportCoordinateSystem> InspectCoordinateSystemAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var declared = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("spatialReference", out _);
            return ImportCoordinateSystem.FromDetection(
                TryReadWkid(root), declared ? ImportCrsProvenance.FileMetadata : ImportCrsProvenance.Unknown);
        }
        catch (JsonException)
        {
            return ImportCoordinateSystem.FromDetection(null, ImportCrsProvenance.Unknown);
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

    private static int NormalizeWkid(int wkid) => SpatialReferenceExtensions.NormalizeWebMercatorSrid(wkid);

    private static NtsGeometry? ParseGeometry(
        JsonElement geometry,
        GeometryFactory factory,
        int maxVertices,
        int maxRings)
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
            var coordinates = ReadCoordinates(points, maxVertices);
            return coordinates.Length == 0 ? null : factory.CreateMultiPointFromCoords(coordinates);
        }

        // Polyline: {"paths": [[[x, y], ...], ...]}
        if (geometry.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Array)
        {
            var lines = ReadPartsAsLineStrings(paths, factory, maxVertices);
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
            return ParsePolygon(rings, factory, maxVertices, maxRings);
        }

        return null;
    }

    private static NtsGeometry? ParsePolygon(
        JsonElement rings,
        GeometryFactory factory,
        int maxVertices,
        int maxRings)
    {
        // Classify rings via Orientation.IsCCW, assign each hole to its smallest-area covering
        // shell, and normalize the resulting geometry to right-hand-rule (CCW exterior, CW holes).
        var shells = new List<LinearRing>();
        var holes = new List<LinearRing>();
        var totalRings = 0;
        var totalVertices = 0;

        foreach (var ringElement in rings.EnumerateArray())
        {
            if (++totalRings > maxRings)
            {
                throw new InvalidDataException(
                    $"Polygon ring count exceeds the maximum of {maxRings:N0}.");
            }

            var coordinates = ReadCoordinates(ringElement, maxVertices - totalVertices);
            totalVertices += coordinates.Length;
            if (coordinates.Length < 3)
            {
                continue;
            }

            var closed = EnsureClosed(coordinates);
            if (closed.Length < 4)
            {
                continue;
            }

            var ring = factory.CreateLinearRing(closed);
            if (Orientation.IsCCW(closed))
            {
                holes.Add(ring);
            }
            else
            {
                shells.Add(ring);
            }
        }

        if (shells.Count == 0 && holes.Count == 0)
        {
            return null;
        }

        if (shells.Count == 0)
        {
            shells.AddRange(holes);
            holes.Clear();
        }

        var holeAssignments = new Dictionary<LinearRing, List<LinearRing>>();

        foreach (var hole in holes)
        {
            var holePolygon = factory.CreatePolygon(hole);
            var assignedShell = FindSmallestCoveringShell(
                shells,
                factory,
                shellPolygon => shellPolygon.Covers(holePolygon));

            if (assignedShell == null)
            {
                var holePoint = holePolygon.InteriorPoint;
                assignedShell = FindSmallestCoveringShell(
                    shells,
                    factory,
                    shellPolygon => shellPolygon.Covers(holePoint));
            }

            if (assignedShell == null)
            {
                shells.Add(hole);
                continue;
            }

            if (!holeAssignments.TryGetValue(assignedShell, out var assignedHoles))
            {
                assignedHoles = new List<LinearRing>();
                holeAssignments[assignedShell] = assignedHoles;
            }

            assignedHoles.Add(hole);
        }

        if (holes.Count == 0 && shells.Count > 1)
        {
            AssignSameWindingHoles(shells, holeAssignments, factory);
        }

        var polygons = shells.Select(shell =>
        {
            holeAssignments.TryGetValue(shell, out var assigned);
            return factory.CreatePolygon(shell, assigned?.ToArray() ?? Array.Empty<LinearRing>());
        }).ToArray();

        NtsGeometry? result = polygons.Length switch
        {
            0 => null,
            1 => polygons[0],
            _ => factory.CreateMultiPolygon(polygons)
        };

        return result == null ? null : RingOrientationNormalizer.Normalize(result, wantExteriorCcw: true);
    }

    private static void AssignSameWindingHoles(
        List<LinearRing> shells,
        Dictionary<LinearRing, List<LinearRing>> holeAssignments,
        GeometryFactory factory)
    {
        var parents = new Dictionary<LinearRing, LinearRing>();
        foreach (var candidate in shells)
        {
            var candidatePolygon = factory.CreatePolygon(candidate);
            var parent = FindSmallestCoveringShell(
                shells,
                factory,
                shellPolygon => shellPolygon.Area > candidatePolygon.Area &&
                    (shellPolygon.Covers(candidatePolygon) || shellPolygon.Covers(candidatePolygon.InteriorPoint)),
                exclude: candidate);
            if (parent != null)
            {
                parents[candidate] = parent;
            }
        }

        var holes = new List<LinearRing>();
        foreach (var (candidate, parent) in parents)
        {
            var depth = 1;
            var ancestor = parent;
            while (parents.TryGetValue(ancestor, out var nextAncestor))
            {
                depth++;
                ancestor = nextAncestor;
            }

            // Alternate holes and islands by containment depth, independent of ring order.
            if (depth % 2 == 0)
            {
                continue;
            }

            if (!holeAssignments.TryGetValue(parent, out var assignedHoles))
            {
                assignedHoles = new List<LinearRing>();
                holeAssignments[parent] = assignedHoles;
            }

            assignedHoles.Add(candidate);
            holes.Add(candidate);
        }

        shells.RemoveAll(holes.Contains);
    }

    private static LinearRing? FindSmallestCoveringShell(
        List<LinearRing> shells,
        GeometryFactory factory,
        Func<Polygon, bool> covers,
        LinearRing? exclude = null)
    {
        LinearRing? smallestShell = null;
        var smallestArea = double.PositiveInfinity;

        foreach (var shell in shells)
        {
            if (ReferenceEquals(shell, exclude))
            {
                continue;
            }

            var shellPolygon = factory.CreatePolygon(shell);
            if (shellPolygon.Area < smallestArea && covers(shellPolygon))
            {
                smallestShell = shell;
                smallestArea = shellPolygon.Area;
            }
        }

        return smallestShell;
    }

    private static LineString[] ReadPartsAsLineStrings(JsonElement parts, GeometryFactory factory, int maxVertices)
    {
        var lines = new List<LineString>();
        var totalVertices = 0;
        foreach (var part in parts.EnumerateArray())
        {
            var coordinates = ReadCoordinates(part, maxVertices - totalVertices);
            totalVertices += coordinates.Length;
            if (coordinates.Length >= 2)
            {
                lines.Add(factory.CreateLineString(coordinates));
            }
        }

        return lines.ToArray();
    }

    private static Coordinate[] ReadCoordinates(JsonElement coordinateArray, int maxVertices)
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

            if (coordinates.Count >= maxVertices)
            {
                throw new InvalidDataException("Geometry vertex count exceeds the maximum allowed.");
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

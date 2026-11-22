// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.Domain;
using Honua.Scene.Terrain;
using Honua.TestKit.Attributes;

namespace Honua.Scene.UnitTests.Features.Infrastructure.Scene;

public sealed class QuantizedMeshTileWriterTests
{
    private static readonly TerrainGeographicBounds MauiTileBounds = new(-156.75, 20.5, -156.25, 21.0);

    [UnitTest]
    public void Write_AndRead_RoundTripsHeaderVerticesIndicesAndSortedEdges()
    {
        const int gridSize = 9;
        var elevations = BuildSlopedElevations(gridSize, baseHeight: 15.5, eastSlope: 420.0, northSlope: 280.0);

        var bytes = QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
        {
            Bounds = MauiTileBounds,
            GridSize = gridSize,
            Elevations = elevations,
            IncludeVertexNormals = false
        });

        bytes.Length.Should().BeGreaterThan(QuantizedMeshTileWriter.HeaderByteLength);

        var decoded = QuantizedMeshTileReader.Read(bytes);
        var expectedVertices = gridSize * gridSize;
        var expectedTriangles = (gridSize - 1) * (gridSize - 1) * 2;

        decoded.Uses32BitIndices.Should().BeFalse();
        decoded.U.Should().HaveCount(expectedVertices);
        decoded.V.Should().HaveCount(expectedVertices);
        decoded.Height.Should().HaveCount(expectedVertices);
        decoded.Indices.Should().HaveCount(expectedTriangles * 3);
        decoded.OctEncodedNormals.Should().BeNull();

        decoded.Header.MinimumHeight.Should().BeApproximately(15.5f, 0.01f);
        decoded.Header.MaximumHeight.Should().BeApproximately(715.5f, 0.01f);
        decoded.Header.BoundingSphereRadius.Should().BeGreaterThan(0.0);
        double.IsFinite(decoded.Header.CenterX).Should().BeTrue();
        double.IsFinite(decoded.Header.CenterY).Should().BeTrue();
        double.IsFinite(decoded.Header.CenterZ).Should().BeTrue();
        double.IsFinite(decoded.Header.HorizonOcclusionPointX).Should().BeTrue();
        double.IsFinite(decoded.Header.HorizonOcclusionPointY).Should().BeTrue();
        double.IsFinite(decoded.Header.HorizonOcclusionPointZ).Should().BeTrue();

        AssertEdgesSortedAndOnBoundary(decoded, gridSize);
        AssertAllTrianglesCounterClockwise(decoded);
        AssertBoundingSphereEnclosesAllVertices(decoded, MauiTileBounds);
    }

    [UnitTest]
    public void WriteGzipped_WithOctVertexNormals_RoundTripsExtensionId1WithOutwardNormals()
    {
        const int gridSize = 17;
        var elevations = BuildSlopedElevations(gridSize, baseHeight: 0.0, eastSlope: 100.0, northSlope: 50.0);

        var gzipped = QuantizedMeshTileWriter.WriteGzipped(new QuantizedMeshTileInput
        {
            Bounds = MauiTileBounds,
            GridSize = gridSize,
            Elevations = elevations,
            IncludeVertexNormals = true
        });

        gzipped[0].Should().Be(0x1F);
        gzipped[1].Should().Be(0x8B);

        var decoded = QuantizedMeshTileReader.Read(gzipped);
        decoded.OctEncodedNormals.Should().NotBeNull();
        decoded.OctEncodedNormals!.Length.Should().Be(gridSize * gridSize * 2);

        var centerMagnitude = Math.Sqrt(
            (decoded.Header.CenterX * decoded.Header.CenterX) +
            (decoded.Header.CenterY * decoded.Header.CenterY) +
            (decoded.Header.CenterZ * decoded.Header.CenterZ));
        var radialX = decoded.Header.CenterX / centerMagnitude;
        var radialY = decoded.Header.CenterY / centerMagnitude;
        var radialZ = decoded.Header.CenterZ / centerMagnitude;

        for (var i = 0; i < gridSize * gridSize; i++)
        {
            var (nx, ny, nz) = QuantizedMeshEllipsoid.OctDecode(
                decoded.OctEncodedNormals[i * 2],
                decoded.OctEncodedNormals[(i * 2) + 1]);
            var dot = (nx * radialX) + (ny * radialY) + (nz * radialZ);
            dot.Should().BeGreaterThan(0.5, "surface normals must point outward from the WGS84 ellipsoid");
        }
    }

    [UnitTest]
    public void Write_WithMoreThan65536Vertices_UsesAlignedIndexData32AndEdgeIndices32()
    {
        const int gridSize = 257;
        var elevations = BuildSlopedElevations(gridSize, baseHeight: 10.0, eastSlope: 50.0, northSlope: 25.0);

        var bytes = QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
        {
            Bounds = MauiTileBounds,
            GridSize = gridSize,
            Elevations = elevations,
            IncludeVertexNormals = false
        });

        var decoded = QuantizedMeshTileReader.Read(bytes);
        decoded.Uses32BitIndices.Should().BeTrue();
        decoded.U.Should().HaveCount(gridSize * gridSize);
        decoded.Indices.Should().HaveCount((gridSize - 1) * (gridSize - 1) * 6);
        AssertEdgesSortedAndOnBoundary(decoded, gridSize);
    }

    [UnitTest]
    public void Write_AllNoDataGrid_ProducesValidSeaLevelMesh()
    {
        const int gridSize = 5;
        var elevations = new double?[gridSize * gridSize];

        var bytes = QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
        {
            Bounds = new TerrainGeographicBounds { West = -180.0, South = -90.0, East = 0.0, North = 90.0 },
            GridSize = gridSize,
            Elevations = elevations,
            IncludeVertexNormals = true
        });

        var decoded = QuantizedMeshTileReader.Read(bytes);
        decoded.Header.MinimumHeight.Should().Be(0.0f);
        decoded.Header.MaximumHeight.Should().Be(0.0f);
        decoded.Height.Should().OnlyContain(static h => h == 0);
        decoded.OctEncodedNormals.Should().HaveCount(gridSize * gridSize * 2);
        AssertEdgesSortedAndOnBoundary(decoded, gridSize);
    }

    [UnitTest]
    public void Write_LevelZeroHemispheres_AreVisibleToCesiumAboveTheirSurface()
    {
        const int gridSize = 5;
        foreach (var west in new[] { -180.0, 0.0 })
        {
            var bytes = QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
            {
                Bounds = new TerrainGeographicBounds { West = west, South = -90.0, East = west + 180.0, North = 90.0 },
                GridSize = gridSize,
                Elevations = BuildSlopedElevations(gridSize, baseHeight: 100.0, eastSlope: 0.0, northSlope: 0.0)
            });
            var header = QuantizedMeshTileReader.Read(bytes).Header;
            var point = (header.HorizonOcclusionPointX, header.HorizonOcclusionPointY, header.HorizonOcclusionPointZ);

            // Cameras above the equator and outside the unit ellipsoid, in scaled coordinates.
            foreach (var offset in new[] { 1.0, 90.0, 179.0 })
            {
                var angle = (west + offset) * Math.PI / 180.0;
                var camera = (X: 1.1 * Math.Cos(angle), Y: 1.1 * Math.Sin(angle), Z: 0.0);
                IsVisibleToCesium(point, camera).Should().BeTrue();
                IsVisibleToCesium((0.0, 0.0, 0.0), camera).Should().BeFalse();
            }
        }
    }

    private static bool IsVisibleToCesium(
        (double X, double Y, double Z) point,
        (double X, double Y, double Z) camera)
    {
        // Cesium 1.134 EllipsoidalOccluder.isScaledSpacePointVisible, exterior camera case.
        var vx = point.X - camera.X;
        var vy = point.Y - camera.Y;
        var vz = point.Z - camera.Z;
        var vhSquared = (camera.X * camera.X) + (camera.Y * camera.Y) + (camera.Z * camera.Z) - 1.0;
        var vtDotVc = -((vx * camera.X) + (vy * camera.Y) + (vz * camera.Z));
        var occluded = vtDotVc > vhSquared
            && (vtDotVc * vtDotVc) / ((vx * vx) + (vy * vy) + (vz * vz)) > vhSquared;
        return !occluded;
    }

    [UnitTest]
    public void OctEncode_AndOctDecode_RoundTripUnitVectorsAcrossAllHemispheres()
    {
        (double X, double Y, double Z)[] vectors =
        [
            (1.0, 0.0, 0.0),
            (-1.0, 0.0, 0.0),
            (0.0, 1.0, 0.0),
            (0.0, -1.0, 0.0),
            (0.0, 0.0, 1.0),
            (0.0, 0.0, -1.0),
            (0.577350269, 0.577350269, 0.577350269),
            (-0.577350269, 0.577350269, -0.577350269)
        ];

        foreach (var (x, y, z) in vectors)
        {
            var (octX, octY) = QuantizedMeshEllipsoid.OctEncode(x, y, z);
            var (dx, dy, dz) = QuantizedMeshEllipsoid.OctDecode(octX, octY);
            var dot = (x * dx) + (y * dy) + (z * dz);
            dot.Should().BeGreaterThan(0.999);
        }
    }

    [UnitTest]
    public void Write_InvalidInput_ThrowsExpectedException()
    {
        Action badGridSize = () => QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
        {
            Bounds = MauiTileBounds,
            GridSize = 1,
            Elevations = [0.0]
        });
        badGridSize.Should().Throw<ArgumentOutOfRangeException>();

        Action wrongLength = () => QuantizedMeshTileWriter.Write(new QuantizedMeshTileInput
        {
            Bounds = MauiTileBounds,
            GridSize = 3,
            Elevations = [0.0, 1.0]
        });
        wrongLength.Should().Throw<ArgumentException>();
    }

    private static double?[] BuildSlopedElevations(int gridSize, double baseHeight, double eastSlope, double northSlope)
    {
        var elevations = new double?[gridSize * gridSize];
        var steps = gridSize - 1.0;
        for (var row = 0; row < gridSize; row++)
        {
            for (var col = 0; col < gridSize; col++)
            {
                elevations[(row * gridSize) + col] = baseHeight + (col / steps * eastSlope) + (row / steps * northSlope);
            }
        }

        return elevations;
    }

    private static void AssertEdgesSortedAndOnBoundary(QuantizedMeshTile tile, int gridSize)
    {
        tile.WestIndices.Should().HaveCount(gridSize);
        tile.SouthIndices.Should().HaveCount(gridSize);
        tile.EastIndices.Should().HaveCount(gridSize);
        tile.NorthIndices.Should().HaveCount(gridSize);

        tile.WestIndices.Select(i => tile.U[i]).Should().OnlyContain(static u => u == 0);
        tile.WestIndices.Select(i => tile.V[i]).Should().BeInDescendingOrder();

        tile.SouthIndices.Select(i => tile.V[i]).Should().OnlyContain(static v => v == 0);
        tile.SouthIndices.Select(i => tile.U[i]).Should().BeInAscendingOrder();

        tile.EastIndices.Select(i => tile.U[i]).Should().OnlyContain(static u => u == QuantizedMeshTileWriter.MaxQuantizedCoordinate);
        tile.EastIndices.Select(i => tile.V[i]).Should().BeInAscendingOrder();

        tile.NorthIndices.Select(i => tile.V[i]).Should().OnlyContain(static v => v == QuantizedMeshTileWriter.MaxQuantizedCoordinate);
        tile.NorthIndices.Select(i => tile.U[i]).Should().BeInDescendingOrder();
    }

    private static void AssertAllTrianglesCounterClockwise(QuantizedMeshTile tile)
    {
        for (var i = 0; i < tile.Indices.Length; i += 3)
        {
            var i0 = tile.Indices[i];
            var i1 = tile.Indices[i + 1];
            var i2 = tile.Indices[i + 2];

            var cross = ((long)(tile.U[i1] - tile.U[i0]) * (tile.V[i2] - tile.V[i0]))
                - ((long)(tile.V[i1] - tile.V[i0]) * (tile.U[i2] - tile.U[i0]));
            cross.Should().BeGreaterThan(0, "triangles must have counter-clockwise winding in (u, v) space");
        }
    }

    private static void AssertBoundingSphereEnclosesAllVertices(QuantizedMeshTile tile, TerrainGeographicBounds bounds)
    {
        var heightRange = tile.Header.MaximumHeight - tile.Header.MinimumHeight;
        for (var i = 0; i < tile.U.Length; i++)
        {
            var lon = bounds.West + ((tile.U[i] / (double)QuantizedMeshTileWriter.MaxQuantizedCoordinate) * (bounds.East - bounds.West));
            var lat = bounds.South + ((tile.V[i] / (double)QuantizedMeshTileWriter.MaxQuantizedCoordinate) * (bounds.North - bounds.South));
            var h = tile.Header.MinimumHeight + ((tile.Height[i] / (double)QuantizedMeshTileWriter.MaxQuantizedCoordinate) * heightRange);

            var ecef = QuantizedMeshEllipsoid.GeodeticToEcef(lon, lat, h);
            var dx = ecef.X - tile.Header.BoundingSphereCenterX;
            var dy = ecef.Y - tile.Header.BoundingSphereCenterY;
            var dz = ecef.Z - tile.Header.BoundingSphereCenterZ;
            var dist = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            dist.Should().BeLessThanOrEqualTo(tile.Header.BoundingSphereRadius + 1.0);
        }
    }
}

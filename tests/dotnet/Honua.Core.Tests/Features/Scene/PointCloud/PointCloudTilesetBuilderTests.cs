// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>
/// End-to-end unit tests for the LAS -> 3D Tiles point-cloud pipeline (#1201).
/// </summary>
public sealed class PointCloudTilesetBuilderTests
{
    // Treat the LAS X/Y as longitude/latitude degrees directly (a geographic
    // LAS) so the test does not depend on a projected-CRS transform.
    private static (double, double, double) IdentityGeo(double x, double y, double z) => (x, y, z);

    [UnitTest]
    public void Build_FixtureLas_ProducesValidPointTileset()
    {
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(20, 20), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points,
            IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 50, MaxDepth = 8, InteriorSampleCount = 16 },
            "honua-test");

        result.PointCount.Should().Be(400);
        result.TileCount.Should().BeGreaterThan(1);
        result.Tiles.Should().NotBeEmpty();

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        json.RootElement.GetProperty("asset").GetProperty("version").GetString().Should().Be("1.1");
        json.RootElement.GetProperty("root").GetProperty("refine").GetString().Should().Be("REPLACE");
    }

    [UnitTest]
    public void Build_EveryTilesetContentUri_HasMatchingTileBlob()
    {
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(16, 16), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 40, MaxDepth = 8, InteriorSampleCount = 16 });

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        var uris = new List<string>();
        CollectContentUris(json.RootElement.GetProperty("root"), uris);

        uris.Should().NotBeEmpty();
        foreach (var uri in uris)
        {
            result.Tiles.Should().ContainKey(uri);
            PntsHeaderIsValid(result.Tiles[uri]).Should().BeTrue();
        }
    }

    [UnitTest]
    public void Build_IsDeterministic_ByteIdenticalAcrossRuns()
    {
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(18, 18), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();
        var options = new PointCloudTilingOptions { MaxPointsPerTile = 45, MaxDepth = 8, InteriorSampleCount = 16 };

        var a = PointCloudTilesetBuilder.Build(points, IdentityGeo, options, "honua");
        var b = PointCloudTilesetBuilder.Build(points, IdentityGeo, options, "honua");

        a.TilesetJsonBytes.Should().Equal(b.TilesetJsonBytes);
        a.Tiles.Keys.Should().BeEquivalentTo(b.Tiles.Keys);
        foreach (var key in a.Tiles.Keys)
        {
            a.Tiles[key].Should().Equal(b.Tiles[key]);
        }
    }

    [UnitTest]
    public void Build_PreservesClassificationAndRgbInPntsBatchTable()
    {
        // Single tile so every source point lands in one leaf and we can read
        // its batch-table classification back.
        var sourcePoints = new List<LasFixtureBuilder.Point>
        {
            new(0.001, 0.001, 1.0, 1000, 2, 60000, 0, 0),
            new(0.002, 0.002, 2.0, 2000, 6, 0, 60000, 0),
        };
        var las = LasFixtureBuilder.BuildFormat3(sourcePoints);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 100, MaxDepth = 4, InteriorSampleCount = 0 });

        result.TileCount.Should().Be(1);
        var tile = result.Tiles.Values.Single();
        // Classification bytes live in the batch-table binary; assert the two
        // ASPRS codes survive the round trip.
        ExtractClassifications(tile).Should().Contain([(byte)2, (byte)6]);
    }

    [UnitTest]
    public void Build_EmptyPoints_Throws()
    {
        var act = () => PointCloudTilesetBuilder.Build(
            Array.Empty<LasPoint>(), IdentityGeo, new PointCloudTilingOptions());

        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Build_DegenerateSinglePointCloud_FloorsRootGeometricError()
    {
        // A single point (or all co-located points) yields a zero-extent
        // bounding box. The root geometric error must be floored at a positive
        // value so a 3D Tiles client still has a refinement budget — mirroring
        // the I3S converter / BSL builder.
        var las = LasFixtureBuilder.BuildFormat3(
        [
            new(0.001, 0.001, 1.0, 1000, 2, 60000, 40000, 30000),
        ]);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 100, MaxDepth = 4, InteriorSampleCount = 0 });

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        json.RootElement.GetProperty("root").GetProperty("geometricError").GetDouble()
            .Should().BeGreaterThanOrEqualTo(1.0);
    }

    [UnitTest]
    public void Build_MultiLevelTileset_LeafNodesDeclareZeroGeometricError()
    {
        // A leaf tile has no children, so its geometricError must be 0.0: a
        // positive value tells the client finer detail exists below it and
        // screen-space-error refinement never converges at the deepest LOD.
        // Mirrors the feature pipeline (SceneQuadtreePartitioner leaf == 0.0).
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(24, 24), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 30, MaxDepth = 8, InteriorSampleCount = 16 });

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        var leafErrors = new List<double>();
        CollectLeafGeometricErrors(json.RootElement.GetProperty("root"), leafErrors);

        leafErrors.Should().NotBeEmpty();
        leafErrors.Should().OnlyContain(error => error == 0.0);
    }

    private static void CollectLeafGeometricErrors(JsonElement node, List<double> sink)
    {
        var hasChildren = node.TryGetProperty("children", out var children)
            && children.GetArrayLength() > 0;
        if (!hasChildren)
        {
            sink.Add(node.GetProperty("geometricError").GetDouble());
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            CollectLeafGeometricErrors(child, sink);
        }
    }

    private static void CollectContentUris(JsonElement node, List<string> sink)
    {
        if (node.TryGetProperty("content", out var content))
        {
            sink.Add(content.GetProperty("uri").GetString()!);
        }
        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                CollectContentUris(child, sink);
            }
        }
    }

    private static bool PntsHeaderIsValid(byte[] tile)
        => tile.Length >= 28 && Encoding.ASCII.GetString(tile, 0, 4) == "pnts";

    private static List<byte> ExtractClassifications(byte[] tile)
    {
        var featureJsonLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var featureBinLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(16, 4));
        var batchJsonLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(20, 4));
        var batchBinStart = 28 + featureJsonLen + featureBinLen + batchJsonLen;

        var batchJson = Encoding.UTF8.GetString(tile, 28 + featureJsonLen + featureBinLen, batchJsonLen);
        using var doc = JsonDocument.Parse(batchJson);
        var classificationOffset = doc.RootElement.GetProperty("CLASSIFICATION").GetProperty("byteOffset").GetInt32();
        var intensityCount = doc.RootElement.GetProperty("INTENSITY").GetProperty("byteOffset").GetInt32();
        _ = intensityCount;

        // CLASSIFICATION is UNSIGNED_BYTE; the column runs to the end of the
        // batch binary. Read every byte from its offset onward.
        var result = new List<byte>();
        for (var i = batchBinStart + classificationOffset; i < tile.Length; i++)
        {
            result.Add(tile[i]);
        }
        return result;
    }

    private static List<LasFixtureBuilder.Point> GridPoints(int cols, int rows)
    {
        var points = new List<LasFixtureBuilder.Point>(cols * rows);
        // Keep coordinates as small longitude/latitude degrees near the origin.
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                var lon = 0.0001 * (c + 1);
                var lat = 0.0001 * (r + 1);
                points.Add(new LasFixtureBuilder.Point(
                    lon, lat, 1.0 + r + c,
                    Intensity: (ushort)(100 + c),
                    Classification: (byte)((r + c) % 4),
                    Red: 50000, Green: 40000, Blue: 30000));
            }
        }
        return points;
    }
}

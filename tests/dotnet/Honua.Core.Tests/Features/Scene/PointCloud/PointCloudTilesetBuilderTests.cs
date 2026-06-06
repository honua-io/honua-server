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
    public void Build_ColorDepthDecidedDatasetWide_AllTilesEncodeIdentically()
    {
        // Regression for the per-tile colour seam (#1): the 8-bit-vs-16-bit RGB
        // interpretation must be decided ONCE for the whole cloud, not inside
        // each tile. Two well-separated clusters land in distinct leaves: cluster
        // A is entirely dark (every channel <= 255) and cluster B contains a
        // channel > 255, so the dataset is genuine 16-bit. Both clusters share a
        // point whose raw colour is exactly 200. Under the dataset-wide decision
        // every tile >>8-scales, so raw 200 encodes to 0 in BOTH tiles. With the
        // old per-tile heuristic the all-dark tile would have copied 200 verbatim
        // while the bright tile scaled it to 0 — a visible seam.
        var darkCluster = new List<LasFixtureBuilder.Point>
        {
            new(0.0001, 0.0001, 1.0, 100, 2, Red: 200, Green: 100, Blue: 50),
            new(0.0002, 0.0002, 1.0, 100, 2, Red: 10, Green: 20, Blue: 30),
        };
        var brightCluster = new List<LasFixtureBuilder.Point>
        {
            new(0.9001, 0.9001, 1.0, 100, 2, Red: 200, Green: 100, Blue: 50),
            new(0.9002, 0.9002, 1.0, 100, 2, Red: 60000, Green: 30000, Blue: 10000),
        };
        var sourcePoints = new List<LasFixtureBuilder.Point>();
        sourcePoints.AddRange(darkCluster);
        sourcePoints.AddRange(brightCluster);

        var las = LasFixtureBuilder.BuildFormat3(sourcePoints);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 2, MaxDepth = 8, InteriorSampleCount = 0 });

        // The two clusters must split across at least two leaf tiles so the
        // per-tile-vs-dataset-wide distinction is actually exercised.
        result.TileCount.Should().BeGreaterThan(1);

        var rawTwoHundredEncodings = new List<byte>();
        foreach (var tile in result.Tiles.Values)
        {
            foreach (var (r, g, b) in ExtractRgb(tile))
            {
                // Locate the encoded colour for the shared raw (200,100,50) point.
                // Under the dataset-wide 16-bit decision it is (0,0,0); a per-tile
                // 8-bit flip in the dark tile would leave it (200,100,50).
                if ((r == 0 && g == 0 && b == 0) || (r == 200 && g == 100 && b == 50))
                {
                    rawTwoHundredEncodings.Add(r);
                }
            }
        }

        // Both clusters contributed a raw (200,100,50) point; every occurrence
        // must have collapsed to 0 (dataset-wide 16-bit), never copied verbatim.
        rawTwoHundredEncodings.Should().NotBeEmpty();
        rawTwoHundredEncodings.Should().OnlyContain(value => value == 0,
            "the dataset-wide colour decision must encode raw 200 as 200>>8 == 0 in every tile");
    }

    [UnitTest]
    public void Build_InteriorSampleCountZero_InteriorRegionEnclosesDescendantHeights()
    {
        // With InteriorSampleCount = 0 every interior node is content-less and is
        // dropped from the content map; its region vertical extent must still
        // enclose the [min,max] heights of its content-bearing leaf descendants,
        // or a conformant 3D Tiles client can cull visible geometry. GridPoints
        // assigns z = 1 + r + c, so leaf heights vary across the quadtree and a
        // naive [0,0] interior slab would be detectably wrong.
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(24, 24), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();

        var result = PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointsPerTile = 30, MaxDepth = 8, InteriorSampleCount = 0 });

        using var json = JsonDocument.Parse(Encoding.UTF8.GetString(result.TilesetJsonBytes));
        var root = json.RootElement.GetProperty("root");

        // The configuration must actually produce content-less interior nodes.
        CountContentLessInteriorNodes(root).Should().BeGreaterThan(0);

        AssertRegionEnclosesDescendantHeights(root);
    }

    private static int CountContentLessInteriorNodes(JsonElement node)
    {
        var hasChildren = node.TryGetProperty("children", out var children)
            && children.GetArrayLength() > 0;
        var hasContent = node.TryGetProperty("content", out _);
        var count = hasChildren && !hasContent ? 1 : 0;
        if (hasChildren)
        {
            foreach (var child in children.EnumerateArray())
            {
                count += CountContentLessInteriorNodes(child);
            }
        }
        return count;
    }

    // Returns the union [min,max] height of every content-bearing node in the
    // subtree (Region layout: [west, south, east, north, minHeight, maxHeight]).
    private static (bool Found, double Min, double Max) AssertRegionEnclosesDescendantHeights(JsonElement node)
    {
        var found = false;
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;

        var hasContent = node.TryGetProperty("content", out _);
        if (hasContent)
        {
            var region = node.GetProperty("boundingVolume").GetProperty("region");
            found = true;
            min = region[4].GetDouble();
            max = region[5].GetDouble();
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                var (childFound, childMin, childMax) = AssertRegionEnclosesDescendantHeights(child);
                if (childFound)
                {
                    found = true;
                    if (childMin < min) min = childMin;
                    if (childMax > max) max = childMax;
                }
            }
        }

        if (found)
        {
            var region = node.GetProperty("boundingVolume").GetProperty("region");
            region[4].GetDouble().Should().BeLessThanOrEqualTo(
                min, "the node region floor must enclose every content-bearing descendant");
            region[5].GetDouble().Should().BeGreaterThanOrEqualTo(
                max, "the node region ceiling must enclose every content-bearing descendant");
        }

        return (found, min, max);
    }

    [UnitTest]
    public void Build_PointCountOverCap_ThrowsLasFormatException()
    {
        // A tiny cap rejects the cloud BEFORE the per-point buffer is allocated,
        // so a malformed/oversized LAS cannot drive an unbounded allocation.
        var las = LasFixtureBuilder.BuildFormat3(GridPoints(4, 4), scale: 1e-7);
        var points = LasPointCloudReader.ReadPoints(las).ToList();
        points.Count.Should().Be(16);

        var act = () => PointCloudTilesetBuilder.Build(
            points, IdentityGeo,
            new PointCloudTilingOptions { MaxPointCount = 8 });

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be(Honua.Core.Features.Scene.Domain.SceneGenerationErrorCodes.ModelAssetInvalid);
    }

    private static List<(byte R, byte G, byte B)> ExtractRgb(byte[] tile)
    {
        var featureJsonLen = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("RGB", out var rgb))
        {
            return [];
        }

        var pointsLength = doc.RootElement.GetProperty("POINTS_LENGTH").GetInt32();
        var rgbByteOffset = rgb.GetProperty("byteOffset").GetInt32();
        var rgbStart = 28 + featureJsonLen + rgbByteOffset;

        var result = new List<(byte, byte, byte)>(pointsLength);
        for (var i = 0; i < pointsLength; i++)
        {
            var o = rgbStart + (i * 3);
            result.Add((tile[o], tile[o + 1], tile[o + 2]));
        }
        return result;
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>
/// Unit tests for the deterministic PNTS point-tile writer (#1201).
/// </summary>
public sealed class PntsTileWriterTests
{
    [UnitTest]
    public void Build_EmitsValidPntsHeaderWithAlignedSections()
    {
        var tile = PntsTileWriter.Build(SamplePoints());

        Encoding.ASCII.GetString(tile, 0, 4).Should().Be("pnts");
        BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(4, 4)).Should().Be(1u);
        BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(8, 4)).Should().Be((uint)tile.Length);

        var featureJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var featureBinLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(16, 4));
        var batchJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(20, 4));
        var batchBinLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(24, 4));

        (28 + featureJsonLen).Should().Match(v => v % 8 == 0, "feature-table JSON must pad to an 8-byte boundary");
        (28 + featureJsonLen + featureBinLen + batchJsonLen).Should().Match(v => v % 8 == 0);
        (28 + featureJsonLen + featureBinLen + batchJsonLen + batchBinLen).Should().Be((uint)tile.Length);
    }

    [UnitTest]
    public void Build_FeatureTableAdvertisesPositionRgbAndRtcCenter()
    {
        var tile = PntsTileWriter.Build(SamplePoints());
        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("POINTS_LENGTH").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("RTC_CENTER").GetArrayLength().Should().Be(3);
        doc.RootElement.TryGetProperty("POSITION", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("RGB", out _).Should().BeTrue();
    }

    [UnitTest]
    public void Build_BatchTablePreservesIntensityAndClassification()
    {
        var tile = PntsTileWriter.Build(SamplePoints());
        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var featureBinLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(16, 4));
        var batchJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(20, 4));

        var batchJsonStart = 28 + featureJsonLen + featureBinLen;
        var json = Encoding.UTF8.GetString(tile, batchJsonStart, batchJsonLen);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("INTENSITY").GetProperty("componentType").GetString().Should().Be("UNSIGNED_SHORT");
        doc.RootElement.GetProperty("CLASSIFICATION").GetProperty("componentType").GetString().Should().Be("UNSIGNED_BYTE");
    }

    [UnitTest]
    public void Build_IsByteIdenticalAcrossRuns()
    {
        var a = PntsTileWriter.Build(SamplePoints());
        var b = PntsTileWriter.Build(SamplePoints());

        a.Should().Equal(b);
    }

    [UnitTest]
    public void Build_NoColor_OmitsRgbSemantic()
    {
        var points = new List<PntsPoint>
        {
            new(6_378_137.0, 0.0, 0.0, 100, 2, HasColor: false, 0, 0, 0),
            new(6_378_138.0, 1.0, 1.0, 200, 5, HasColor: false, 0, 0, 0),
        };

        var tile = PntsTileWriter.Build(points);
        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("RGB", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Build_EmptyPoints_Throws()
    {
        var act = () => PntsTileWriter.Build(Array.Empty<PntsPoint>());

        act.Should().Throw<ArgumentException>();
    }

    private static List<PntsPoint> SamplePoints() =>
    [
        new(6_378_137.0, 10.0, 20.0, 1200, 2, HasColor: true, 60000, 30000, 10000),
        new(6_378_137.5, 11.0, 21.0, 800, 6, HasColor: true, 40000, 50000, 20000),
        new(6_378_138.0, 12.0, 22.0, 4000, 1, HasColor: true, 10000, 10000, 65000),
    ];
}

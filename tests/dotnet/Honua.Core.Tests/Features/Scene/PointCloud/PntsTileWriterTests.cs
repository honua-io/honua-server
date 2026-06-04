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
        var tile = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);

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
    public void Build_TotalByteLengthIsEightByteAligned()
    {
        // 3 colored points => batch binary is 3*2 (INTENSITY) + 3*1
        // (CLASSIFICATION) = 9 bytes, which alone would leave the tile
        // unaligned; the writer must pad the trailing binary so the whole tile
        // byteLength is a multiple of 8 (3D Tiles PNTS spec).
        var tile = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);

        (tile.Length % 8).Should().Be(0, "the PNTS tile byteLength must be 8-byte aligned");
        BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(8, 4))
            .Should().Be((uint)tile.Length, "the header byteLength must equal the padded tile length");
    }

    [UnitTest]
    public void Build_FeatureTableAdvertisesPositionRgbAndRtcCenter()
    {
        var tile = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);
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
        var tile = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);
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
        var a = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);
        var b = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);

        a.Should().Equal(b);
    }

    [UnitTest]
    public void Build_ColoredOddByteCount_BatchTableJsonHeaderIsEightByteAligned()
    {
        // 3 colored points => feature binary = 3*12 (POSITION) + 3*3 (RGB) = 45
        // bytes, which is NOT a multiple of 8. Without padding the feature binary
        // the batch-table JSON header would start misaligned. Assert the writer
        // pads the feature binary so the batch JSON offset is 8-aligned.
        var tile = PntsTileWriter.Build(SamplePoints(), eightBitColor: false);

        var featureJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var featureBinLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(16, 4));

        var batchJsonOffset = 28 + featureJsonLen + featureBinLen;
        (batchJsonOffset % 8).Should().Be(0u, "the batch-table JSON header must begin on an 8-byte boundary");
        (featureBinLen % 8).Should().Be(0u, "the feature-table binary length (offset 16) must be 8-byte padded");
    }

    [UnitTest]
    public void Build_OddUncoloredCount_BatchTableJsonHeaderIsEightByteAligned()
    {
        // 3 uncolored points => feature binary = 3*12 = 36 bytes (not a multiple
        // of 8). The writer must pad it so the batch-table JSON header aligns.
        var points = new List<PntsPoint>
        {
            new(6_378_137.0, 0.0, 0.0, 100, 2, HasColor: false, 0, 0, 0),
            new(6_378_138.0, 1.0, 1.0, 200, 5, HasColor: false, 0, 0, 0),
            new(6_378_139.0, 2.0, 2.0, 300, 6, HasColor: false, 0, 0, 0),
        };

        var tile = PntsTileWriter.Build(points, eightBitColor: false);

        var featureJsonLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var featureBinLen = BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(16, 4));

        ((28 + featureJsonLen + featureBinLen) % 8).Should().Be(0u, "the batch-table JSON header must begin on an 8-byte boundary");
        (featureBinLen % 8).Should().Be(0u, "the feature-table binary length must be 8-byte padded");
        (tile.Length % 8).Should().Be(0, "the whole tile byteLength must remain 8-aligned");
    }

    [UnitTest]
    public void Build_EightBitColorInSixteenBitField_RendersNonBlackRgb()
    {
        // Some LAS producers store 8-bit (0-255) colour unscaled in the 16-bit
        // RGB fields. A blind >>8 would map these to 0 (black). When the
        // dataset-wide decision is 8-bit, the writer copies the low byte verbatim.
        var points = new List<PntsPoint>
        {
            new(6_378_137.0, 0.0, 0.0, 100, 2, HasColor: true, 200, 150, 50),
            new(6_378_138.0, 1.0, 1.0, 200, 5, HasColor: true, 10, 255, 128),
        };

        var tile = PntsTileWriter.Build(points, eightBitColor: true);

        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);
        using var doc = JsonDocument.Parse(json);
        var rgbByteOffset = doc.RootElement.GetProperty("RGB").GetProperty("byteOffset").GetInt32();

        var rgbStart = 28 + featureJsonLen + rgbByteOffset;
        // First point's RGB must be the verbatim 8-bit values, not >>8'd to 0.
        tile[rgbStart].Should().Be(200);
        tile[rgbStart + 1].Should().Be(150);
        tile[rgbStart + 2].Should().Be(50);
    }

    [UnitTest]
    public void Build_TrueSixteenBitColor_ScalesToEightBit()
    {
        // When any component exceeds 255 the cloud is genuine 16-bit colour and
        // every channel is scaled by >>8.
        var points = new List<PntsPoint>
        {
            new(6_378_137.0, 0.0, 0.0, 100, 2, HasColor: true, 60000, 30000, 10000),
            new(6_378_138.0, 1.0, 1.0, 200, 5, HasColor: true, 256, 256, 256),
        };

        var tile = PntsTileWriter.Build(points, eightBitColor: false);

        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);
        using var doc = JsonDocument.Parse(json);
        var rgbByteOffset = doc.RootElement.GetProperty("RGB").GetProperty("byteOffset").GetInt32();

        var rgbStart = 28 + featureJsonLen + rgbByteOffset;
        tile[rgbStart].Should().Be((byte)(60000 >> 8));
        tile[rgbStart + 1].Should().Be((byte)(30000 >> 8));
        tile[rgbStart + 2].Should().Be((byte)(10000 >> 8));
    }

    [UnitTest]
    public void Build_NoColor_OmitsRgbSemantic()
    {
        var points = new List<PntsPoint>
        {
            new(6_378_137.0, 0.0, 0.0, 100, 2, HasColor: false, 0, 0, 0),
            new(6_378_138.0, 1.0, 1.0, 200, 5, HasColor: false, 0, 0, 0),
        };

        var tile = PntsTileWriter.Build(points, eightBitColor: false);
        var featureJsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tile.AsSpan(12, 4));
        var json = Encoding.UTF8.GetString(tile, 28, featureJsonLen);

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("RGB", out _).Should().BeFalse();
    }

    [UnitTest]
    public void Build_EmptyPoints_Throws()
    {
        var act = () => PntsTileWriter.Build(Array.Empty<PntsPoint>(), eightBitColor: false);

        act.Should().Throw<ArgumentException>();
    }

    private static List<PntsPoint> SamplePoints() =>
    [
        new(6_378_137.0, 10.0, 20.0, 1200, 2, HasColor: true, 60000, 30000, 10000),
        new(6_378_137.5, 11.0, 21.0, 800, 6, HasColor: true, 40000, 50000, 20000),
        new(6_378_138.0, 12.0, 22.0, 4000, 1, HasColor: true, 10000, 10000, 65000),
    ];
}

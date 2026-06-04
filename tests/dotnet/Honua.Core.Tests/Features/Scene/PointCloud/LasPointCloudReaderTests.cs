// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>
/// Unit tests for the pure-managed LAS reader (#1201).
/// </summary>
public sealed class LasPointCloudReaderTests
{
    [UnitTest]
    public void ReadHeader_ParsesVersionFormatAndCounts()
    {
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());

        var header = LasPointCloudReader.ReadHeader(las);

        header.VersionMajor.Should().Be(1);
        header.VersionMinor.Should().Be(2);
        header.PointDataRecordFormat.Should().Be(3);
        header.PointCount.Should().Be(3UL);
        header.HasColor.Should().BeTrue();
    }

    [UnitTest]
    public void ReadPoints_DescalesPositionAndPreservesAttributes()
    {
        var points = SamplePoints();
        var las = LasFixtureBuilder.BuildFormat3(points, scale: 0.01, offsetX: 100.0, offsetY: 200.0, offsetZ: 5.0);

        var decoded = LasPointCloudReader.ReadPoints(las).ToList();

        decoded.Should().HaveCount(3);
        decoded[0].X.Should().BeApproximately(points[0].X, 0.01);
        decoded[0].Y.Should().BeApproximately(points[0].Y, 0.01);
        decoded[0].Z.Should().BeApproximately(points[0].Z, 0.01);
        decoded[0].Classification.Should().Be(points[0].Classification);
        decoded[0].Intensity.Should().Be(points[0].Intensity);
        decoded[0].Red.Should().Be(points[0].Red);
        decoded[0].Green.Should().Be(points[0].Green);
        decoded[0].Blue.Should().Be(points[0].Blue);
        decoded[0].HasColor.Should().BeTrue();
    }

    [UnitTest]
    public void ReadHeader_NonLasSignature_Throws()
    {
        var garbage = new byte[300];
        garbage[0] = (byte)'N';

        var act = () => LasPointCloudReader.ReadHeader(garbage);

        act.Should().Throw<LasFormatException>().Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void ReadHeader_TooShort_Throws()
    {
        var act = () => LasPointCloudReader.ReadHeader(new byte[10]);

        act.Should().Throw<LasFormatException>();
    }

    [UnitTest]
    public void ReadHeader_CompressedLaz_RejectsWithClearError()
    {
        var laz = LasFixtureBuilder.MarkCompressed(LasFixtureBuilder.BuildFormat3(SamplePoints()));

        var act = () => LasPointCloudReader.ReadHeader(laz);

        act.Should().Throw<LasFormatException>()
            .WithMessage("*LAZ*");
    }

    [UnitTest]
    public void ReadPoints_TruncatedData_Throws()
    {
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        var truncated = las[..^10];

        var act = () => LasPointCloudReader.ReadPoints(truncated).ToList();

        act.Should().Throw<LasFormatException>();
    }

    [UnitTest]
    public void ReadPoints_OffsetBeyondEndOfFile_ThrowsStableLasFormatException()
    {
        // OffsetToPointData (a uint32 at byte 96) is attacker-controlled. A value
        // beyond the buffer must surface the stable LasFormatException rather
        // than a checked-cast OverflowException or an AsSpan range exception.
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        BinaryPrimitives.WriteUInt32LittleEndian(las.AsSpan(96, 4), (uint)(las.Length + 1));

        var act = () => LasPointCloudReader.ReadPoints(las).ToList();

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void ReadPoints_OffsetExceedingInt32_ThrowsStableLasFormatException()
    {
        // A uint32 OffsetToPointData larger than int.MaxValue would overflow a
        // narrowing int cast; long/ulong validation must intercept it first.
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        BinaryPrimitives.WriteUInt32LittleEndian(las.AsSpan(96, 4), uint.MaxValue);

        var act = () => LasPointCloudReader.ReadPoints(las).ToList();

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void ReadPoints_PointCountOverflowsRequiredBytes_ThrowsStableLasFormatException()
    {
        // PointCount is an attacker-controlled uint64 and PointRecordLength a
        // uint16; PointCount * recordLength can overflow ulong and wrap small,
        // bypassing the truncation guard and admitting a near-infinite loop / a
        // raw AsSpan ArgumentOutOfRangeException. A declared count of
        // ulong.MaxValue over a tiny body must surface the stable
        // LasFormatException, not an OverflowException/ArgumentOutOfRangeException.
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        var header = LasPointCloudReader.ReadHeader(las) with { PointCount = ulong.MaxValue };

        var act = () => LasPointCloudReader.ReadPoints(las, header).ToList();

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void ReadPoints_PointCountNearInt64Max_ThrowsStableLasFormatException()
    {
        // 2^63 records with a small record length also wrap the product; the
        // overflow-safe bound (PointCount > availableBytes / recordLength) must
        // reject it without overflowing.
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        var header = LasPointCloudReader.ReadHeader(las) with { PointCount = 1UL << 63 };

        var act = () => LasPointCloudReader.ReadPoints(las, header).ToList();

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    [UnitTest]
    public void ReadPoints_WithCallerHeaderPointRecordLengthZero_ThrowsStableLasFormatException()
    {
        // The public ReadPoints(byte[], LasHeader) overload trusts a caller-supplied
        // header. A zero PointRecordLength would skip the per-record overflow guard
        // (recordLength != 0), leave requiredBytes at 0, pass the truncation check,
        // and then throw a raw ArgumentOutOfRangeException from record.Slice(0, 4)
        // on a zero-length span. The reader must instead reject it with the stable
        // LasFormatException contract.
        var las = LasFixtureBuilder.BuildFormat3(SamplePoints());
        var header = LasPointCloudReader.ReadHeader(las) with { PointRecordLength = 0 };

        var act = () => LasPointCloudReader.ReadPoints(las, header).ToList();

        act.Should().Throw<LasFormatException>()
            .Which.Code.Should().Be("SCENE_MODEL_ASSET_INVALID");
    }

    private static List<LasFixtureBuilder.Point> SamplePoints() =>
    [
        new(100.5, 200.5, 5.5, 1200, 2, 60000, 30000, 10000),
        new(101.0, 201.0, 6.0, 800, 6, 40000, 50000, 20000),
        new(102.25, 202.75, 7.5, 4000, 1, 10000, 10000, 65000),
    ];
}

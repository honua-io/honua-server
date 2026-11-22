// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Scene;

public sealed class CopcPointCloudReaderTests
{
    [UnitTest]
    public void TryReadCopcInfo_GenuinePdalFixtures_ParsesCopcInfoVlr()
    {
        var fixtures = new (string FileName, byte ExpectedFormat, bool ExpectedHasColor)[]
        {
            ("genuine-6.copc.laz", 6, false),
            ("genuine-7.copc.laz", 7, true),
            ("genuine-8.copc.laz", 8, true)
        };

        foreach (var (fileName, expectedFormat, expectedHasColor) in fixtures)
        {
            var path = RepositoryPaths.Resolve(
                "tests", "dotnet", "Honua.Worker.Gdal.Tests", "Fixtures", "PointCloudProof", fileName);
            var bytes = File.ReadAllBytes(path);

            LasPointCloudReader.IsCopc(bytes).Should().BeTrue(fileName);
            LasPointCloudReader.TryReadCopcInfo(bytes, out var info, out var error).Should().BeTrue(error);
            info.Should().NotBeNull();
            info!.VersionMajor.Should().Be(1);
            info.VersionMinor.Should().Be(4);
            info.PointDataRecordFormat.Should().Be(expectedFormat);
            info.HasColor.Should().Be(expectedHasColor);
            info.PointCount.Should().BeGreaterThan(0);
            info.HalfSize.Should().BeGreaterThan(0);
            info.Spacing.Should().BeGreaterThan(0);
            info.RootHierarchySize.Should().BeGreaterThan(0);
        }
    }

    [UnitTest]
    public void TryReadCopcInfo_UntiledRawLas_ReturnsFalseWhilePreservingLasfSignature()
    {
        var path = RepositoryPaths.Resolve(
            "tests", "dotnet", "Honua.Worker.Gdal.Tests", "Fixtures", "PointCloudProof", "geographic.las");
        var bytes = File.ReadAllBytes(path);

        PointCloudCompressionDetector.HasLasfSignature(bytes).Should().BeTrue();
        LasPointCloudReader.TryReadCopcInfo(bytes, out var info, out var error).Should().BeFalse();
        info.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public void TryReadCopcInfo_CompressedLas14WithoutCopcVlr_ReturnsFalse()
    {
        var synthetic = new byte[512];
        "LASF"u8.CopyTo(synthetic);
        synthetic[24] = 1;
        synthetic[25] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(synthetic.AsSpan(94, 2), 375);
        BinaryPrimitives.WriteUInt32LittleEndian(synthetic.AsSpan(100, 4), 1);
        synthetic[104] = 6 | 0x80; // LAZ compressed format 6, but first VLR user_id is not "copc"
        "laszip encoded"u8.CopyTo(synthetic.AsSpan(375 + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(synthetic.AsSpan(375 + 18, 2), 22204);

        PointCloudCompressionDetector.HasLasfSignature(synthetic).Should().BeTrue();
        LasPointCloudReader.TryReadCopcInfo(synthetic, out _, out var error).Should().BeFalse();
        error.Should().Contain("copc");
    }

    [UnitTest]
    public void ReadCopcInfo_MalformedBytes_ThrowsLasFormatException()
    {
        var action = () => LasPointCloudReader.ReadCopcInfo("not-a-copc-file"u8);
        action.Should().Throw<LasFormatException>();
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>Source metadata reads must not require point decoding or decompression.</summary>
public sealed class PointCloudSourceHeaderTests
{
    [UnitTest]
    public void ReadSourceHeader_CompressedCopcPrefix_PreservesHeaderMetadataWithoutDecodingPoints()
    {
        var bytes = File.ReadAllBytes(Path.Join(AppContext.BaseDirectory,
            "Features", "Scene", "PointCloud", "Fixtures", "source-format6.copc.laz"));

        // The genuine COPC file has point data, but only its public header reaches this API.
        var header = LasPointCloudReader.ReadSourceHeader(bytes.AsSpan(0, 375));

        header.VersionMinor.Should().Be(4);
        header.PointDataRecordFormat.Should().Be(6);
        header.PointCount.Should().Be(3UL);
        header.MinX.Should().BeApproximately(9.9999989, 1e-10);
        header.MaxX.Should().BeApproximately(10.0000013, 1e-10);
        header.MinZ.Should().Be(29.875);
        header.MaxZ.Should().Be(31.375);
        // This proves header metadata only; COPC identity and CRS require bounded VLR reads.
    }

    [UnitTest]
    public void ReadSourceHeader_UncompressedHeaderOnly_PreservesExistingScalarHeaderBehavior()
    {
        var bytes = LasFixtureBuilder.BuildFormat3([
            new LasFixtureBuilder.Point(1, 2, 3, 10, 2, 0, 0, 0)]);

        var header = LasPointCloudReader.ReadSourceHeader(bytes.AsSpan(0, 227));

        header.PointDataRecordFormat.Should().Be(3);
        header.PointCount.Should().Be(1UL);
        header.MinX.Should().Be(1);
        header.MaxZ.Should().Be(3);
    }
}

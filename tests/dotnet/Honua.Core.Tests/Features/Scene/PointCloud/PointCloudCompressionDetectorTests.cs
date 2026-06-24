// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Scene.PointCloud;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Scene.PointCloud;

/// <summary>
/// Unit coverage for <see cref="PointCloudCompressionDetector"/> (#1854): the
/// non-throwing buffer classifier the ingest dispatch path uses to decide
/// whether an uploaded point cloud must be routed through the out-of-tree
/// decompression worker before the managed tiler can read it.
/// </summary>
public sealed class PointCloudCompressionDetectorTests
{
    [UnitTest]
    public void IsCompressed_UncompressedLas_ReturnsFalse()
    {
        var las = LasFixtureBuilder.BuildFormat3(SinglePoint());

        PointCloudCompressionDetector.IsCompressed(las).Should().BeFalse();
        PointCloudCompressionDetector.HasLasfSignature(las).Should().BeTrue();
    }

    [UnitTest]
    public void IsCompressed_LazCompressedFlag_ReturnsTrue()
    {
        // laszip sets bit 7 (0x80) of the point-data-record-format byte.
        var laz = LasFixtureBuilder.MarkCompressed(LasFixtureBuilder.BuildFormat3(SinglePoint()));

        PointCloudCompressionDetector.IsCompressed(laz).Should().BeTrue();
    }

    [UnitTest]
    public void IsCompressed_CopcIndicatorFlag_ReturnsTrue()
    {
        // Bit 6 (0x40) is the COPC indicator; treated as compressed for dispatch.
        var copc = LasFixtureBuilder.BuildFormat3(SinglePoint());
        copc[104] = (byte)(copc[104] | 0x40);

        PointCloudCompressionDetector.IsCompressed(copc).Should().BeTrue();
    }

    [UnitTest]
    public void IsCompressed_NonLasfBuffer_ReturnsFalse()
    {
        // A non-LASF buffer is left for the reader to reject; the detector must
        // not mis-classify arbitrary bytes whose offset-104 byte happens to have
        // a high bit set as a compressed point cloud.
        var noise = new byte[256];
        for (var i = 0; i < noise.Length; i++)
        {
            noise[i] = 0xFF;
        }

        PointCloudCompressionDetector.IsCompressed(noise).Should().BeFalse();
        PointCloudCompressionDetector.HasLasfSignature(noise).Should().BeFalse();
    }

    [UnitTest]
    public void IsCompressed_BufferShorterThanFormatByte_ReturnsFalse()
    {
        PointCloudCompressionDetector.IsCompressed(Encoding.ASCII.GetBytes("LASF")).Should().BeFalse();
        PointCloudCompressionDetector.IsCompressed(ReadOnlySpan<byte>.Empty).Should().BeFalse();
    }

    private static IReadOnlyList<LasFixtureBuilder.Point> SinglePoint()
        => [new LasFixtureBuilder.Point(-122.5, 37.5, 12.0, 100, 2, 10, 20, 30)];
}

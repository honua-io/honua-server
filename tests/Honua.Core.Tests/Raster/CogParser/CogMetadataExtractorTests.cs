// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Unit tests for CogMetadataExtractor.
/// Validates extent computation with TIFF-spec ascending tag order
/// where tag 33550 (PixelScale) precedes tag 33922 (Tiepoint).
/// </summary>
public class CogMetadataExtractorTests
{
    [Fact]
    public async Task ReadMetadataAsync_PixelScaleBeforeTiepoint_ComputesCorrectExtent()
    {
        // Arrange — synthetic COG with tags in TIFF-spec ascending order.
        // Tag 33550 (ModelPixelScale) at IFD position 8 comes BEFORE
        // tag 33922 (ModelTiepoint) at IFD position 9.
        // The tiepoint is (-10, 50) and scale is (0.01, 0.01).
        var tiffData = BuildSyntheticCogBytes(
            width: 1000, height: 500,
            tileWidth: 256, tileHeight: 256,
            scaleX: 0.01, scaleY: 0.01,
            tiepointX: -10.0, tiepointY: 50.0);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert — extent must be computed from tiepoint + scale,
        // NOT from (0,0) + scale (which is what happens if scale is
        // eagerly computed before the tiepoint tag is processed).
        metadata.Extent.XMin.Should().BeApproximately(-10.0, 1e-10);
        metadata.Extent.YMax.Should().BeApproximately(50.0, 1e-10);
        metadata.Extent.XMax.Should().BeApproximately(0.0, 1e-10);   // -10 + 0.01*1000
        metadata.Extent.YMin.Should().BeApproximately(45.0, 1e-10);  // 50 - 0.01*500
    }

    [Fact]
    public async Task ReadMetadataAsync_NoGeoTags_FallsBackToUnitExtent()
    {
        // Arrange — synthetic COG with no georeferencing tags
        var tiffData = BuildSyntheticCogBytesNoGeo(width: 512, height: 256);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert — fallback unit extent
        metadata.Width.Should().Be(512);
        metadata.Height.Should().Be(256);
        metadata.Extent.XMin.Should().Be(0);
        metadata.Extent.XMax.Should().Be(512);
        metadata.Extent.YMin.Should().Be(0);
        metadata.Extent.YMax.Should().Be(256);
    }

    [Fact]
    public async Task ReadMetadataAsync_ReportsCorrectIfdOffset()
    {
        // Arrange
        var tiffData = BuildSyntheticCogBytesNoGeo(width: 100, height: 100);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert — the IFD offset should be the actual file offset (8), not 0
        metadata.OverviewLevels.Should().HaveCount(1);
        metadata.OverviewLevels[0].IfdOffset.Should().Be(8);
    }

    [Fact]
    public async Task ReadMetadataAsync_BothGeoKeys_PrefersProjectedCrs()
    {
        // Arrange — synthetic COG with GeoKeyDirectoryTag containing both
        // GeographicTypeGeoKey (2048) = 4326 and ProjectedCSTypeGeoKey (3072) = 3857.
        // GeoKeys are in ascending KeyID order so 2048 appears before 3072.
        // The extractor must prefer the projected CRS.
        var tiffData = BuildSyntheticCogBytesWithGeoKeys(
            width: 256, height: 256,
            geographicSrid: 4326, projectedSrid: 3857);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert — SRID must be 3857 (projected), not 4326 (geographic)
        metadata.Srid.Should().Be(3857);
    }

    [Fact]
    public async Task ReadMetadataAsync_OnlyGeographicGeoKey_ReturnsGeographicSrid()
    {
        // Arrange — COG with only GeographicTypeGeoKey, no ProjectedCSTypeGeoKey
        var tiffData = BuildSyntheticCogBytesWithGeoKeys(
            width: 256, height: 256,
            geographicSrid: 4326, projectedSrid: 0);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert — falls back to geographic CRS
        metadata.Srid.Should().Be(4326);
    }

    [Fact]
    public async Task ReadMetadataAsync_Float32WithSampleFormat_ReportsFloat32()
    {
        // Arrange — 32-bit image with SampleFormat = 3 (float)
        var tiffData = BuildSyntheticCogBytesWithSampleFormat(
            width: 256, height: 256, bitsPerSample: 32, sampleFormat: 3);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert
        metadata.PixelType.Should().Be("float32");
    }

    [Fact]
    public async Task ReadMetadataAsync_Int16WithSampleFormat_ReportsInt16()
    {
        // Arrange — 16-bit image with SampleFormat = 2 (signed integer)
        var tiffData = BuildSyntheticCogBytesWithSampleFormat(
            width: 256, height: 256, bitsPerSample: 16, sampleFormat: 2);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert
        metadata.PixelType.Should().Be("int16");
    }

    [Fact]
    public async Task ReadMetadataAsync_UInt32WithSampleFormat_ReportsUInt32()
    {
        // Arrange — 32-bit image with SampleFormat = 1 (unsigned integer)
        var tiffData = BuildSyntheticCogBytesWithSampleFormat(
            width: 256, height: 256, bitsPerSample: 32, sampleFormat: 1);

        var reader = new InMemoryRangeReader(tiffData);
        var extractor = new CogMetadataExtractor();

        // Act
        var metadata = await extractor.ReadMetadataAsync(reader, "test-bucket", "test.tif");

        // Assert
        metadata.PixelType.Should().Be("uint32");
    }

    /// <summary>
    /// Builds a synthetic classic TIFF (little-endian) with georeferencing tags
    /// in TIFF-spec ascending order. Tag 33550 comes before tag 33922.
    /// </summary>
    private static byte[] BuildSyntheticCogBytes(
        int width, int height, int tileWidth, int tileHeight,
        double scaleX, double scaleY, double tiepointX, double tiepointY)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: little-endian classic TIFF, first IFD at offset 8
        writer.Write((ushort)0x4949); // "II"
        writer.Write((ushort)42);     // classic TIFF magic
        writer.Write((uint)8);        // first IFD at offset 8

        // IFD with 9 entries in ascending tag order
        const int entryCount = 9;
        writer.Write((ushort)entryCount);

        // External data offsets: IFD ends at 8 + 2 + 9*12 + 4 = 122
        const uint scaleDataOffset = 122;
        const uint tiepointDataOffset = 122 + 24; // 146

        // Entries in ascending tag order (TIFF spec requirement)
        WriteIfdEntry(writer, 256, 4, 1, (uint)width);            // ImageWidth
        WriteIfdEntry(writer, 257, 4, 1, (uint)height);           // ImageLength
        WriteIfdEntry(writer, 259, 3, 1, 1);                      // Compression = NONE
        WriteIfdEntry(writer, 322, 4, 1, (uint)tileWidth);        // TileWidth
        WriteIfdEntry(writer, 323, 4, 1, (uint)tileHeight);       // TileLength
        WriteIfdEntry(writer, 324, 4, 1, 5000);                   // TileOffsets (dummy)
        WriteIfdEntry(writer, 325, 4, 1, 1000);                   // TileByteCounts (dummy)
        WriteIfdEntry(writer, 33550, 12, 3, scaleDataOffset);     // ModelPixelScaleTag
        WriteIfdEntry(writer, 33922, 12, 6, tiepointDataOffset);  // ModelTiepointTag

        // Next IFD offset = 0 (single-level COG)
        writer.Write((uint)0);

        // External data: PixelScale (3 doubles: scaleX, scaleY, scaleZ)
        writer.Write(scaleX);
        writer.Write(scaleY);
        writer.Write(0.0);

        // External data: Tiepoint (6 doubles: I, J, K, X, Y, Z)
        writer.Write(0.0);         // I
        writer.Write(0.0);         // J
        writer.Write(0.0);         // K
        writer.Write(tiepointX);   // X
        writer.Write(tiepointY);   // Y
        writer.Write(0.0);         // Z

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal TIFF with no georeferencing tags.
    /// </summary>
    private static byte[] BuildSyntheticCogBytesNoGeo(int width, int height)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);

        const int entryCount = 7;
        writer.Write((ushort)entryCount);

        WriteIfdEntry(writer, 256, 4, 1, (uint)width);
        WriteIfdEntry(writer, 257, 4, 1, (uint)height);
        WriteIfdEntry(writer, 259, 3, 1, 1);
        WriteIfdEntry(writer, 322, 4, 1, 256);
        WriteIfdEntry(writer, 323, 4, 1, 256);
        WriteIfdEntry(writer, 324, 4, 1, 5000);
        WriteIfdEntry(writer, 325, 4, 1, 1000);

        writer.Write((uint)0); // no next IFD

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a synthetic TIFF with a GeoKeyDirectoryTag containing both
    /// GeographicTypeGeoKey and/or ProjectedCSTypeGeoKey.
    /// </summary>
    private static byte[] BuildSyntheticCogBytesWithGeoKeys(
        int width, int height, int geographicSrid, int projectedSrid)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Header: little-endian classic TIFF, first IFD at offset 8
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);

        // Count keys to include
        var keyCount = 0;
        if (geographicSrid > 0) keyCount++;
        if (projectedSrid > 0) keyCount++;

        // IFD entries: 7 base + 1 GeoKeyDirectoryTag = 8
        const int entryCount = 8;
        writer.Write((ushort)entryCount);

        // External data offset: 8 + 2 + 8*12 + 4 = 110
        const uint geoKeyDataOffset = 110;

        // GeoKey data size: header (4 shorts) + keys (4 shorts each)
        var geoKeyShortCount = (uint)(4 + keyCount * 4);

        // IFD entries in ascending tag order
        WriteIfdEntry(writer, 256, 4, 1, (uint)width);
        WriteIfdEntry(writer, 257, 4, 1, (uint)height);
        WriteIfdEntry(writer, 259, 3, 1, 1);                           // Compression = NONE
        WriteIfdEntry(writer, 322, 4, 1, 256);                         // TileWidth
        WriteIfdEntry(writer, 323, 4, 1, 256);                         // TileLength
        WriteIfdEntry(writer, 324, 4, 1, 5000);                        // TileOffsets (dummy)
        WriteIfdEntry(writer, 325, 4, 1, 1000);                        // TileByteCounts (dummy)
        WriteIfdEntry(writer, 34735, 3, geoKeyShortCount, geoKeyDataOffset); // GeoKeyDirectoryTag

        // Next IFD offset = 0
        writer.Write((uint)0);

        // External data: GeoKey directory
        // Header: version=1, revision=1, minor=0, numberOfKeys
        writer.Write((ushort)1);          // KeyDirectoryVersion
        writer.Write((ushort)1);          // KeyRevision
        writer.Write((ushort)0);          // MinorRevision
        writer.Write((ushort)keyCount);   // NumberOfKeys

        // Keys in ascending KeyID order (GeoTIFF spec requirement)
        if (geographicSrid > 0)
        {
            writer.Write((ushort)2048);               // GeographicTypeGeoKey
            writer.Write((ushort)0);                   // TIFFTagLocation = 0 (inline)
            writer.Write((ushort)1);                   // Count
            writer.Write((ushort)geographicSrid);      // Value
        }

        if (projectedSrid > 0)
        {
            writer.Write((ushort)3072);               // ProjectedCSTypeGeoKey
            writer.Write((ushort)0);                   // TIFFTagLocation = 0 (inline)
            writer.Write((ushort)1);                   // Count
            writer.Write((ushort)projectedSrid);       // Value
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds a minimal TIFF with BitsPerSample and SampleFormat tags for pixel type testing.
    /// </summary>
    private static byte[] BuildSyntheticCogBytesWithSampleFormat(
        int width, int height, int bitsPerSample, ushort sampleFormat)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write((uint)8);

        // 9 entries: width, height, bitsPerSample, compression, tileWidth, tileHeight,
        //            tileOffsets, tileByteCounts, sampleFormat
        const int entryCount = 9;
        writer.Write((ushort)entryCount);

        // Entries in ascending tag order
        WriteIfdEntry(writer, 256, 4, 1, (uint)width);                  // ImageWidth
        WriteIfdEntry(writer, 257, 4, 1, (uint)height);                 // ImageLength
        WriteIfdEntry(writer, 258, 3, 1, (uint)bitsPerSample);          // BitsPerSample
        WriteIfdEntry(writer, 259, 3, 1, 1);                            // Compression = NONE
        WriteIfdEntry(writer, 322, 4, 1, 256);                          // TileWidth
        WriteIfdEntry(writer, 323, 4, 1, 256);                          // TileLength
        WriteIfdEntry(writer, 324, 4, 1, 5000);                         // TileOffsets (dummy)
        WriteIfdEntry(writer, 325, 4, 1, 1000);                         // TileByteCounts (dummy)
        WriteIfdEntry(writer, 339, 3, 1, (uint)sampleFormat);           // SampleFormat

        writer.Write((uint)0); // no next IFD
        return ms.ToArray();
    }

    private static void WriteIfdEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint valueOrOffset)
    {
        writer.Write(tag);
        writer.Write(type);
        writer.Write(count);
        writer.Write(valueOrOffset);
    }

    /// <summary>
    /// In-memory ICloudRangeReader for unit testing without cloud dependencies.
    /// </summary>
    private sealed class InMemoryRangeReader : ICloudRangeReader
    {
        private readonly byte[] _data;

        public InMemoryRangeReader(byte[] data) => _data = data;

        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, _data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            var result = new byte[bytesToRead];
            if (bytesToRead > 0)
            {
                Buffer.BlockCopy(_data, (int)offset, result, 0, bytesToRead);
            }
            return Task.FromResult(result);
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, _data.Length - (int)offset);
            var bytesToRead = Math.Min(length, available);
            return Task.FromResult<Stream>(new MemoryStream(_data, (int)offset, bytesToRead));
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)_data.Length);
    }
}

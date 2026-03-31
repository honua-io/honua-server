// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Reads COG metadata from cloud storage via range requests.
/// Implements <see cref="ICogMetadataReader"/> with AOT-safe TIFF IFD parsing.
/// </summary>
public sealed class CogMetadataExtractor : ICogMetadataReader
{
    // Initial read size: enough for header + first IFD with up to 100 entries
    private const int InitialReadSize = 4096;
    private const int MaxOverviewLevels = 30;

    /// <inheritdoc />
    public async Task<CogMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        // Step 1: Read header to determine TIFF format and first IFD offset
        var headerBytes = await reader.ReadRangeAsync(bucket, key, 0, InitialReadSize, cancellationToken).ConfigureAwait(false);
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(headerBytes);

        // Step 2: Walk IFD chain — each IFD represents one resolution level
        var overviewLevels = new List<CogOverviewLevel>();
        var currentOffset = firstIfdOffset;
        var levelIndex = 0;

        // Base image properties (from first IFD)
        int width = 0, height = 0, bandCount = 1, tileWidth = 256, tileHeight = 256;
        ushort compression = TiffConstants.CompressionNone;
        int bitsPerSample = 8;
        ushort sampleFormat = TiffConstants.SampleFormatUnsigned;
        string pixelType = "uint8";
        int srid = 0;
        double xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        double pixelScaleX = 0, pixelScaleY = 0;
        bool hasPixelScale = false;

        while (currentOffset > 0 && levelIndex < MaxOverviewLevels)
        {
            // Read the IFD — estimate 100 entries initially
            var ifdReadSize = parser.CalculateIfdReadSize(100);
            byte[] ifdBytes;
            var ifdFileOffset = currentOffset; // preserve actual file offset for metadata

            // Check if we already have the data in our initial read
            if (currentOffset < headerBytes.Length && currentOffset + ifdReadSize <= headerBytes.Length)
            {
                ifdBytes = headerBytes;
            }
            else
            {
                ifdBytes = await reader.ReadRangeAsync(bucket, key, currentOffset, ifdReadSize, cancellationToken).ConfigureAwait(false);
                currentOffset = 0; // ifdBytes is now relative to 0
            }

            var (entries, nextIfdOffset) = parser.ParseIfd(ifdBytes.AsSpan((int)(currentOffset > 0 ? currentOffset : 0)));

            // Extract metadata from IFD entries
            int ifdWidth = 0, ifdHeight = 0, ifdTileWidth = 256, ifdTileHeight = 256;
            ushort ifdCompression = TiffConstants.CompressionNone;
            ushort samplesPerPixel = 1;
            IfdEntry? tileOffsetsEntry = null;
            IfdEntry? tileByteCountsEntry = null;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case TiffConstants.TagImageWidth:
                        ifdWidth = (int)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagImageLength:
                        ifdHeight = (int)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagTileWidth:
                        ifdTileWidth = (int)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagTileLength:
                        ifdTileHeight = (int)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagCompression:
                        ifdCompression = (ushort)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagSamplesPerPixel:
                        samplesPerPixel = (ushort)entry.ValueOrOffset;
                        break;
                    case TiffConstants.TagTileOffsets:
                        tileOffsetsEntry = entry;
                        break;
                    case TiffConstants.TagTileByteCounts:
                        tileByteCountsEntry = entry;
                        break;
                    case TiffConstants.TagBitsPerSample when levelIndex == 0:
                        if (entry.Count >= 1)
                        {
                            var bpsData = await ReadIntArrayFromEntryAsync(
                                reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                            if (bpsData.Length > 0)
                            {
                                bitsPerSample = bpsData[0];
                            }
                        }
                        break;
                    case TiffConstants.TagSampleFormat when levelIndex == 0:
                        if (entry.Count >= 1)
                        {
                            var sfData = await ReadIntArrayFromEntryAsync(
                                reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                            if (sfData.Length > 0)
                            {
                                sampleFormat = (ushort)sfData[0];
                            }
                        }
                        break;
                    case TiffConstants.TagGeoKeyDirectoryTag when levelIndex == 0:
                        srid = await ExtractSridFromGeoKeysAsync(reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        break;
                    case TiffConstants.TagModelTiepointTag when levelIndex == 0:
                        (xMin, yMax) = await ExtractTiepointAsync(reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        break;
                    case TiffConstants.TagModelPixelScaleTag when levelIndex == 0:
                        (pixelScaleX, pixelScaleY) = await ExtractPixelScaleAsync(reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        hasPixelScale = true;
                        break;
                }
            }

            // First IFD = base image
            if (levelIndex == 0)
            {
                width = ifdWidth;
                height = ifdHeight;
                bandCount = samplesPerPixel;
                tileWidth = ifdTileWidth;
                tileHeight = ifdTileHeight;
                compression = ifdCompression;

                // Classify pixel type now that both BitsPerSample and SampleFormat are collected.
                pixelType = ClassifyPixelType(bitsPerSample, sampleFormat);

                // Compute extent from tiepoint + scale.
                // Both tags are now fully collected regardless of IFD entry order
                // (tag 33550 PixelScale sorts before 33922 Tiepoint in TIFF spec).
                if (hasPixelScale && width > 0 && height > 0)
                {
                    xMax = xMin + pixelScaleX * width;
                    yMin = yMax - pixelScaleY * height;
                }
                else if (xMax == 0 && xMin == 0 && width > 0)
                {
                    // Fallback: unit extent when no georeferencing tags present
                    xMax = width;
                    yMin = 0;
                    yMax = height;
                }
            }

            // Read tile offsets and byte counts
            long[] tileOffsets = [];
            int[] tileByteCounts = [];

            if (tileOffsetsEntry.HasValue && tileByteCountsEntry.HasValue)
            {
                tileOffsets = await ReadLongArrayFromEntryAsync(
                    reader, bucket, key, tileOffsetsEntry.Value, parser, cancellationToken).ConfigureAwait(false);
                tileByteCounts = await ReadIntArrayFromEntryAsync(
                    reader, bucket, key, tileByteCountsEntry.Value, parser, cancellationToken).ConfigureAwait(false);
            }

            overviewLevels.Add(new CogOverviewLevel(
                levelIndex, ifdWidth, ifdHeight, ifdFileOffset,
                tileOffsets, tileByteCounts));

            currentOffset = nextIfdOffset;
            levelIndex++;
        }

        if (width == 0 || height == 0)
        {
            throw new InvalidDataException("COG file does not contain valid image dimensions.");
        }

        return new CogMetadata(
            width, height, bandCount, pixelType, srid,
            TiffConstants.GetCompressionName(compression),
            tileWidth, tileHeight,
            overviewLevels.ToArray(),
            new RasterExtent { XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, Srid = srid });
    }

    private static async Task<long[]> ReadLongArrayFromEntryAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.IsInline)
        {
            if (entry.Count == 1)
            {
                return [entry.ValueOrOffset];
            }

            return parser.ReadInlineLongArray(entry.ValueOrOffset, (int)entry.Count, entry.Type);
        }

        var typeSize = TiffConstants.GetTypeSize(entry.Type);
        var totalBytes = (int)entry.Count * typeSize;
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        return parser.ReadLongArray(data, (int)entry.Count, entry.Type);
    }

    private static async Task<int[]> ReadIntArrayFromEntryAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.IsInline)
        {
            if (entry.Count == 1)
            {
                return [(int)entry.ValueOrOffset];
            }

            return parser.ReadInlineIntArray(entry.ValueOrOffset, (int)entry.Count, entry.Type);
        }

        var typeSize = TiffConstants.GetTypeSize(entry.Type);
        var totalBytes = (int)entry.Count * typeSize;
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        return parser.ReadIntArray(data, (int)entry.Count, entry.Type);
    }

    private static async Task<int> ExtractSridFromGeoKeysAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 4)
        {
            return 0;
        }

        byte[] geoKeyData;
        if (entry.IsInline)
        {
            return 0; // GeoKey directory is always external for meaningful content
        }

        var totalBytes = (int)entry.Count * TiffConstants.GetTypeSize(entry.Type);
        geoKeyData = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);

        // GeoKey directory: header (4 shorts) + entries (4 shorts each)
        // Each entry: KeyID, TIFFTagLocation, Count, Value_Offset
        var numKeys = ReadUInt16(geoKeyData, 6, parser.IsLittleEndian);
        int geographicSrid = 0;
        int projectedSrid = 0;

        for (var i = 0; i < numKeys; i++)
        {
            var keyOffset = 8 + i * 8;
            if (keyOffset + 8 > geoKeyData.Length) break;

            var keyId = ReadUInt16(geoKeyData, keyOffset, parser.IsLittleEndian);
            var tiffTagLocation = ReadUInt16(geoKeyData, keyOffset + 2, parser.IsLittleEndian);
            var value = ReadUInt16(geoKeyData, keyOffset + 6, parser.IsLittleEndian);

            // Collect both CRS keys; prefer projected over geographic.
            // GeoKeys are in ascending KeyID order so GeographicTypeGeoKey (2048)
            // appears before ProjectedCSTypeGeoKey (3072). Value 32767 means
            // "user-defined" and should not be used as an EPSG code.
            if (tiffTagLocation == 0 && value != 0 && value != 32767)
            {
                if (keyId == TiffConstants.GeoKeyProjectedCSTypeGeoKey)
                {
                    projectedSrid = value;
                }
                else if (keyId == TiffConstants.GeoKeyGeographicTypeGeoKey)
                {
                    geographicSrid = value;
                }
            }
        }

        // Prefer projected CRS (e.g. EPSG:3857) when present; fall back to geographic (e.g. EPSG:4326).
        return projectedSrid > 0 ? projectedSrid : geographicSrid;
    }

    private static async Task<(double X, double Y)> ExtractTiepointAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 6)
        {
            return (0, 0);
        }

        var totalBytes = (int)entry.Count * TiffConstants.GetTypeSize(entry.Type);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);

        // Tiepoint: I, J, K, X, Y, Z — we want X (index 3) and Y (index 4)
        var x = ReadDouble(data, 24, parser.IsLittleEndian);
        var y = ReadDouble(data, 32, parser.IsLittleEndian);
        return (x, y);
    }

    private static async Task<(double ScaleX, double ScaleY)> ExtractPixelScaleAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 3)
        {
            return (1, 1);
        }

        var totalBytes = (int)entry.Count * TiffConstants.GetTypeSize(entry.Type);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);

        var scaleX = ReadDouble(data, 0, parser.IsLittleEndian);
        var scaleY = ReadDouble(data, 8, parser.IsLittleEndian);
        return (scaleX, scaleY);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));

    private static double ReadDouble(ReadOnlySpan<byte> data, int offset, bool isLittleEndian)
        => isLittleEndian
            ? BinaryPrimitives.ReadDoubleLittleEndian(data.Slice(offset))
            : BinaryPrimitives.ReadDoubleBigEndian(data.Slice(offset));

    private static string ClassifyPixelType(int bitsPerSample, ushort sampleFormat)
    {
        var prefix = sampleFormat switch
        {
            TiffConstants.SampleFormatFloat => "float",
            TiffConstants.SampleFormatSigned => "int",
            _ => "uint"
        };
        return $"{prefix}{bitsPerSample}";
    }
}

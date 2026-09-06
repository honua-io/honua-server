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

    // Initial per-IFD entry-count estimate; IFDs reporting more entries are re-read at exact size.
    private const int EstimatedIfdEntries = 100;

    // Ceiling for external IFD array lengths (tile offsets/byte counts). A 1,000,000 x 1,000,000 px
    // base level with 256 px tiles is ~15.3M tiles; 16M elements comfortably covers legitimate COGs
    // while rejecting crafted Count values that would otherwise drive multi-GB allocations.
    private const long MaxExternalArrayElements = 16L * 1024 * 1024;

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
        int predictor = TilePixelLayout.PredictorNone;
        ushort sampleFormat = TiffConstants.SampleFormatUnsigned;
        string pixelType = "uint8";
        int srid = 0;
        double xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        double pixelScaleX = 0, pixelScaleY = 0;
        bool hasPixelScale = false;
        double[]? modelTransformation = null;
        double tiepointI = 0, tiepointJ = 0;
        int rasterType = 1; // RasterPixelIsArea is the GeoTIFF default.
        int photometricInterpretation = 1, planarConfiguration = 1;
        string? noData = null;

        while (currentOffset > 0 && levelIndex < MaxOverviewLevels)
        {
            // Read the IFD — estimate 100 entries initially
            var ifdReadSize = parser.CalculateIfdReadSize(EstimatedIfdEntries);
            byte[] ifdBytes;
            int ifdSpanOffset;
            var ifdFileOffset = currentOffset; // preserve actual file offset for metadata

            // Check if we already have the data in our initial read
            if (currentOffset < headerBytes.Length && currentOffset + ifdReadSize <= headerBytes.Length)
            {
                ifdBytes = headerBytes;
                ifdSpanOffset = (int)currentOffset;
            }
            else
            {
                ifdBytes = await reader.ReadRangeAsync(bucket, key, currentOffset, ifdReadSize, cancellationToken).ConfigureAwait(false);
                ifdSpanOffset = 0;
            }

            // If the IFD declares more entries than estimated, re-read it at the exact size so all
            // entries and the next-IFD pointer are parsed instead of slicing past a short buffer.
            var declaredEntryCount = parser.ReadIfdEntryCount(ifdBytes.AsSpan(ifdSpanOffset));
            if (declaredEntryCount is > EstimatedIfdEntries and <= TiffIfdParser.MaxIfdEntryCount)
            {
                ifdBytes = await reader.ReadRangeAsync(
                    bucket, key, ifdFileOffset, parser.CalculateIfdReadSize(declaredEntryCount), cancellationToken).ConfigureAwait(false);
                ifdSpanOffset = 0;
            }

            var (entries, nextIfdOffset) = parser.ParseIfd(ifdBytes.AsSpan(ifdSpanOffset));

            // Extract metadata from IFD entries
            int ifdWidth = 0, ifdHeight = 0, ifdTileWidth = 256, ifdTileHeight = 256;
            ushort ifdCompression = TiffConstants.CompressionNone;
            ushort samplesPerPixel = 1;
            var ifdPredictor = TilePixelLayout.PredictorNone;
            IfdEntry? tileOffsetsEntry = null;
            IfdEntry? tileByteCountsEntry = null;

            foreach (var entry in entries)
            {
                switch (entry.Tag)
                {
                    case TiffConstants.TagPhotometricInterpretation when levelIndex == 0:
                        photometricInterpretation = (int)entry.ValueOrOffset;
                        break;
                    case 284 when levelIndex == 0: // PlanarConfiguration
                        planarConfiguration = (int)entry.ValueOrOffset;
                        break;
                    case 42113 when levelIndex == 0: // GDAL_NODATA
                        if (entry.Type != TiffConstants.TypeAscii || entry.Count is < 1 or > 128)
                        {
                            throw new InvalidDataException("Invalid COG nodata tag.");
                        }
                        var noDataBytes = entry.IsInline
                            ? parser.ReadInlineIntArray(entry.ValueOrOffset, (int)entry.Count, entry.Type)
                                .Select(value => (byte)value).ToArray()
                            : await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset,
                                (int)entry.Count, cancellationToken).ConfigureAwait(false);
                        noData = System.Text.Encoding.ASCII.GetString(noDataBytes).TrimEnd('\0');
                        break;
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
                    case TiffConstants.TagPredictor:
                        ifdPredictor = (int)entry.ValueOrOffset;
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
                                if (bpsData.Any(value => value != bpsData[0]))
                                {
                                    throw new InvalidDataException("COG tiles with mixed sample widths are not supported.");
                                }
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
                                if (sfData.Any(value => value != sfData[0]))
                                {
                                    throw new InvalidDataException("COG tiles with mixed sample formats are not supported.");
                                }
                            }
                        }
                        break;
                    case TiffConstants.TagGeoKeyDirectoryTag when levelIndex == 0:
                        (srid, rasterType) = await ExtractGeoKeysAsync(
                            reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        break;
                    case TiffConstants.TagModelTiepointTag when levelIndex == 0:
                        (tiepointI, tiepointJ, xMin, yMax) = await ExtractTiepointAsync(
                            reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        break;
                    case TiffConstants.TagModelPixelScaleTag when levelIndex == 0:
                        (pixelScaleX, pixelScaleY) = await ExtractPixelScaleAsync(reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
                        hasPixelScale = true;
                        break;
                    case TiffConstants.TagModelTransformationTag when levelIndex == 0:
                        modelTransformation = await ExtractModelTransformationAsync(
                            reader, bucket, key, entry, parser, cancellationToken).ConfigureAwait(false);
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
                predictor = ifdPredictor;

                // Classify pixel type now that both BitsPerSample and SampleFormat are collected.
                pixelType = ClassifyPixelType(bitsPerSample, sampleFormat);

                // Compute extent from the affine GeoTIFF transform. The direct tile path is
                // intentionally north-up only; accepting a rotated/sheared transform while
                // dropping its off-diagonal terms would silently serve the wrong pixels.
                if (modelTransformation is { Length: 16 })
                {
                    if (modelTransformation.Any(value => !double.IsFinite(value))
                        || modelTransformation[1] != 0 || modelTransformation[4] != 0
                        || modelTransformation[2] != 0 || modelTransformation[6] != 0
                        || modelTransformation[12] != 0 || modelTransformation[13] != 0
                        || modelTransformation[14] != 0 || modelTransformation[15] != 1
                        || modelTransformation[0] <= 0
                        || modelTransformation[5] >= 0)
                    {
                        throw new InvalidDataException(
                            "COG ModelTransformation declares a rotated, sheared, or non-north-up grid, which direct tile serving cannot align safely.");
                    }

                    xMin = modelTransformation[3];
                    yMax = modelTransformation[7];
                    pixelScaleX = modelTransformation[0];
                    pixelScaleY = -modelTransformation[5];
                    hasPixelScale = true;
                }

                // Compute extent from tiepoint + scale.
                // Both tags are now fully collected regardless of IFD entry order
                // (tag 33550 PixelScale sorts before 33922 Tiepoint in TIFF spec).
                if (hasPixelScale && width > 0 && height > 0)
                {
                    if (modelTransformation is null)
                    {
                        xMin -= tiepointI * pixelScaleX;
                        yMax += tiepointJ * pixelScaleY;
                    }

                    // RasterPixelIsPoint's tiepoint is the pixel centre. Normalize it to
                    // the upper-left pixel corner before persisting the extent, otherwise
                    // GoogleMapsCompatible tile alignment is shifted by half a pixel.
                    if (rasterType == 2)
                    {
                        xMin -= pixelScaleX / 2d;
                        yMax += pixelScaleY / 2d;
                    }

                    xMax = xMin + pixelScaleX * width;
                    yMin = yMax - pixelScaleY * height;
                    if (!double.IsFinite(xMin) || !double.IsFinite(yMax)
                        || !double.IsFinite(xMax) || !double.IsFinite(yMin)
                        || pixelScaleX <= 0 || pixelScaleY <= 0)
                    {
                        throw new InvalidDataException("COG georeferencing does not describe a finite north-up grid.");
                    }
                }
                // Exact-zero check is intentional and safe here: xMax/xMin are
                // declared with a literal 0 default (line above) and are only
                // ever reassigned via floating-point math in the hasPixelScale
                // branch above, which this else-if cannot reach. There is no
                // computed floating value being compared — only "has this
                // sentinel default been overwritten yet".
                else if (xMax.Equals(0d) && xMin.Equals(0d) && width > 0)
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
            new RasterExtent { XMin = xMin, YMin = yMin, XMax = xMax, YMax = yMax, Srid = srid },
            bitsPerSample, predictor, parser.IsLittleEndian,
            photometricInterpretation, planarConfiguration, noData);
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

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
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

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        return parser.ReadIntArray(data, (int)entry.Count, entry.Type);
    }

    /// <summary>
    /// Computes the byte size of an external IFD array in overflow-safe arithmetic,
    /// rejecting file-controlled <see cref="IfdEntry.Count"/> values that would drive
    /// runaway range reads and allocations.
    /// </summary>
    private static int GetValidatedExternalArrayByteCount(IfdEntry entry)
    {
        if (entry.Count is <= 0 or > MaxExternalArrayElements)
        {
            throw new InvalidDataException(
                $"TIFF IFD entry (tag {entry.Tag}) declares {entry.Count} external values, outside the supported range of 1-{MaxExternalArrayElements}.");
        }

        return checked((int)(entry.Count * TiffConstants.GetTypeSize(entry.Type)));
    }

    private static async Task<(int Srid, int RasterType)> ExtractGeoKeysAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 4)
        {
            return (0, 1);
        }

        byte[] geoKeyData;
        if (entry.IsInline)
        {
            return (0, 1); // GeoKey directory is always external for meaningful content
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        geoKeyData = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);

        // GeoKey directory: header (4 shorts) + entries (4 shorts each)
        // Each entry: KeyID, TIFFTagLocation, Count, Value_Offset
        var numKeys = ReadUInt16(geoKeyData, 6, parser.IsLittleEndian);
        int geographicSrid = 0;
        int projectedSrid = 0;
        var rasterType = 1;

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
                else if (keyId == TiffConstants.GeoKeyRasterTypeGeoKey)
                {
                    rasterType = value;
                }
            }
        }

        // Prefer projected CRS (e.g. EPSG:3857) when present; fall back to geographic (e.g. EPSG:4326).
        return (projectedSrid > 0 ? projectedSrid : geographicSrid, rasterType);
    }

    private static async Task<(double I, double J, double X, double Y)> ExtractTiepointAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 6)
        {
            return (0, 0, 0, 0);
        }

        if (entry.Type != TiffConstants.TypeDouble)
        {
            // Geo-referencing tags MUST use DOUBLE (type 12) so all coordinate reads are
            // at fixed 8-byte offsets. A non-DOUBLE type produces an undersized buffer
            // that would overflow data.Slice(offset) — return the zero origin instead.
            return (0, 0, 0, 0);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);

        // Tiepoint: I, J, K, X, Y, Z — we want X (index 3) and Y (index 4)
        var x = ReadDouble(data, 24, parser.IsLittleEndian);
        var y = ReadDouble(data, 32, parser.IsLittleEndian);
        var i = ReadDouble(data, 0, parser.IsLittleEndian);
        var j = ReadDouble(data, 8, parser.IsLittleEndian);
        return (i, j, x, y);
    }

    private static async Task<double[]?> ExtractModelTransformationAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count != 16 || entry.Type != TiffConstants.TypeDouble || entry.IsInline)
        {
            throw new InvalidDataException(
                "COG ModelTransformationTag must contain sixteen external DOUBLE values.");
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = await reader.ReadRangeAsync(bucket, key, entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        if (data.Length < 16 * sizeof(double))
        {
            throw new InvalidDataException("COG ModelTransformationTag data is truncated.");
        }

        var values = new double[16];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ReadDouble(data, i * sizeof(double), parser.IsLittleEndian);
        }

        return values;
    }

    private static async Task<(double ScaleX, double ScaleY)> ExtractPixelScaleAsync(
        ICloudRangeReader reader, string bucket, string key,
        IfdEntry entry, TiffIfdParser parser, CancellationToken ct)
    {
        if (entry.Count < 3)
        {
            return (1, 1);
        }

        if (entry.Type != TiffConstants.TypeDouble)
        {
            // Same rationale as ExtractTiepointAsync — guard against a non-DOUBLE type
            // declaration that would undersize the buffer relative to the hardcoded read offsets.
            return (1, 1);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
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

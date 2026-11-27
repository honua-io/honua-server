// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

public sealed class CogMetadataExtractor : ICogMetadataReader
{
    // Prefetch 64 KiB so the TIFF header, IFD chain, GeoKeys, and small tile offset tables are
    // typically satisfied from a single cloud range request instead of 20-30 sequential round-trips.
    private const int InitialReadSize = 65536;
    private const int MaxOverviewLevels = 30;
    private const int MaxVisitedIfds = MaxOverviewLevels * 2;

    // Initial per-IFD entry-count estimate; IFDs reporting more entries are re-read at exact size.
    private const int EstimatedIfdEntries = 100;

    // Ceiling for external IFD array lengths (tile offsets/byte counts). A 1,000,000 x 1,000,000 px
    // base level with 256 px tiles is ~15.3M tiles; 16M elements comfortably covers legitimate COGs
    // while rejecting crafted Count values that would otherwise drive multi-GB allocations.
    private const long MaxExternalArrayElements = 16L * 1024 * 1024;

    public async Task<CogMetadata> ReadMetadataAsync(
        ICloudRangeReader reader,
        string bucket,
        string key,
        CancellationToken cancellationToken = default)
    {
        var headerBytes = await reader.ReadRangeAsync(bucket, key, 0, InitialReadSize, cancellationToken).ConfigureAwait(false);
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(headerBytes);
        var source = new HeaderSliceReader(reader, bucket, key, headerBytes, parser);

        var overviewLevels = new List<CogOverviewLevel>();
        var visitedOffsets = new HashSet<long>();
        var baseState = new BaseImageState();
        var currentOffset = firstIfdOffset;

        while (currentOffset > 0 && overviewLevels.Count < MaxOverviewLevels && visitedOffsets.Count < MaxVisitedIfds)
        {
            if (!visitedOffsets.Add(currentOffset))
            {
                throw new InvalidDataException($"Cyclic TIFF IFD offset {currentOffset} detected.");
            }

            var (entries, nextIfdOffset) = await ReadIfdAsync(source, currentOffset, cancellationToken).ConfigureAwait(false);
            if (IsInternalMaskIfd(entries))
            {
                currentOffset = nextIfdOffset;
                continue;
            }

            var level = await ParseLevelAsync(
                    source,
                    new LevelParseContext(entries, currentOffset, overviewLevels.Count, baseState),
                    cancellationToken)
                .ConfigureAwait(false);
            overviewLevels.Add(level);
            currentOffset = nextIfdOffset;
        }

        return BuildMetadata(baseState, overviewLevels, parser.IsLittleEndian);
    }

    private static async Task<(IfdEntry[] Entries, long NextIfdOffset)> ReadIfdAsync(
        HeaderSliceReader source,
        long ifdOffset,
        CancellationToken cancellationToken)
    {
        var estimatedSize = source.Parser.CalculateIfdReadSize(EstimatedIfdEntries);
        var ifdData = await source.ReadForIfdAsync(ifdOffset, estimatedSize, cancellationToken).ConfigureAwait(false);
        var declaredEntryCount = source.Parser.ReadIfdEntryCount(ifdData.Span);
        if (declaredEntryCount is > EstimatedIfdEntries and <= TiffIfdParser.MaxIfdEntryCount)
        {
            var exactSize = source.Parser.CalculateIfdReadSize(declaredEntryCount);
            ifdData = await source.ReadForIfdAsync(ifdOffset, exactSize, cancellationToken).ConfigureAwait(false);
        }

        return source.Parser.ParseIfd(ifdData.Span);
    }

    private static bool IsInternalMaskIfd(IfdEntry[] entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Tag == TiffConstants.TagNewSubfileType)
            {
                return ((uint)entry.ValueOrOffset & TiffConstants.SubfileTypeMaskBit) != 0;
            }
        }

        return false;
    }

    private static async Task<CogOverviewLevel> ParseLevelAsync(
        HeaderSliceReader source,
        LevelParseContext context,
        CancellationToken cancellationToken)
    {
        var levelState = new IfdLevelState();
        foreach (var entry in context.Entries)
        {
            await ApplyEntryAsync(source, new EntryApplyTarget(entry, levelState, context.BaseState, context.LevelIndex == 0), cancellationToken)
                .ConfigureAwait(false);
        }

        if (context.LevelIndex == 0)
        {
            FinalizeBaseImage(context.BaseState, levelState);
        }

        var (tileOffsets, tileByteCounts) = await ReadTileIndexAsync(source, levelState, cancellationToken).ConfigureAwait(false);
        return new CogOverviewLevel(
            context.LevelIndex,
            levelState.Width,
            levelState.Height,
            context.IfdOffset,
            tileOffsets,
            tileByteCounts,
            levelState.JpegTables,
            levelState.Predictor);
    }

    private static async Task ApplyEntryAsync(
        HeaderSliceReader source,
        EntryApplyTarget target,
        CancellationToken cancellationToken)
    {
        if (ApplyLevelScalarEntry(target.Entry, target.Level))
        {
            return;
        }

        if (target.Entry.Tag == TiffConstants.TagJpegTables)
        {
            target.Level.JpegTables = await ReadJpegTablesAsync(source, target.Entry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (target.IsBaseLevel)
        {
            await ApplyBaseOnlyEntryAsync(source, new BaseEntryTarget(target.Entry, target.Base), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ApplyLevelScalarEntry(IfdEntry entry, IfdLevelState level)
    {
        switch (entry.Tag)
        {
            case TiffConstants.TagImageWidth:
                level.Width = (int)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagImageLength:
                level.Height = (int)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagTileWidth:
                level.TileWidth = (int)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagTileLength:
                level.TileHeight = (int)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagCompression:
                level.Compression = (ushort)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagSamplesPerPixel:
                level.SamplesPerPixel = (ushort)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagPredictor:
                level.Predictor = (int)entry.ValueOrOffset;
                return true;
            case TiffConstants.TagTileOffsets:
                level.TileOffsetsEntry = entry;
                return true;
            case TiffConstants.TagTileByteCounts:
                level.TileByteCountsEntry = entry;
                return true;
            default:
                return false;
        }
    }

    private static async Task ApplyBaseOnlyEntryAsync(
        HeaderSliceReader source,
        BaseEntryTarget target,
        CancellationToken cancellationToken)
    {
        var entry = target.Entry;
        var state = target.Base;
        switch (entry.Tag)
        {
            case TiffConstants.TagPhotometricInterpretation:
                state.PhotometricInterpretation = (int)entry.ValueOrOffset;
                break;
            case 284: // PlanarConfiguration
                state.PlanarConfiguration = (int)entry.ValueOrOffset;
                break;
            case 42113: // GDAL_NODATA
                state.NoData = await ReadNoDataTagAsync(source, entry, cancellationToken).ConfigureAwait(false);
                break;
            case TiffConstants.TagBitsPerSample when entry.Count >= 1:
                state.BitsPerSample = await ReadUniformBandValueAsync(source, new UniformBandRead(entry, "COG tiles with mixed sample widths are not supported."), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case TiffConstants.TagSampleFormat when entry.Count >= 1:
                state.SampleFormat = (ushort)await ReadUniformBandValueAsync(source, new UniformBandRead(entry, "COG tiles with mixed sample formats are not supported."), cancellationToken)
                    .ConfigureAwait(false);
                break;
            case TiffConstants.TagGeoKeyDirectoryTag:
                (state.Srid, state.RasterType) = await ExtractGeoKeysAsync(source, entry, cancellationToken).ConfigureAwait(false);
                break;
            case TiffConstants.TagModelTiepointTag:
                (state.TiepointI, state.TiepointJ, state.XMin, state.YMax) = await ExtractTiepointAsync(source, entry, cancellationToken).ConfigureAwait(false);
                break;
            case TiffConstants.TagModelPixelScaleTag:
                (state.PixelScaleX, state.PixelScaleY) = await ExtractPixelScaleAsync(source, entry, cancellationToken).ConfigureAwait(false);
                state.HasPixelScale = true;
                break;
            case TiffConstants.TagModelTransformationTag:
                state.ModelTransformation = await ExtractModelTransformationAsync(source, entry, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static async Task<string> ReadNoDataTagAsync(HeaderSliceReader source, IfdEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Type != TiffConstants.TypeAscii || entry.Count is < 1 or > 128 || (!entry.IsInline && entry.ValueOrOffset < 0))
        {
            throw new InvalidDataException("Invalid COG nodata tag.");
        }

        var noDataBytes = entry.IsInline
            ? source.Parser.ReadInlineIntArray(entry.ValueOrOffset, (int)entry.Count, entry.Type).Select(value => (byte)value).ToArray()
            : (await source.ReadExactAsync(entry.ValueOrOffset, (int)entry.Count, cancellationToken).ConfigureAwait(false)).ToArray();
        return System.Text.Encoding.ASCII.GetString(noDataBytes).TrimEnd('\0');
    }

    private static async Task<int> ReadUniformBandValueAsync(
        HeaderSliceReader source,
        UniformBandRead read,
        CancellationToken cancellationToken)
    {
        var values = await ReadIntArrayFromEntryAsync(source, read.Entry, cancellationToken).ConfigureAwait(false);
        if (values.Length == 0)
        {
            return 0;
        }

        if (values.Any(value => value != values[0]))
        {
            throw new InvalidDataException(read.MixedErrorMessage);
        }

        return values[0];
    }

    private static void FinalizeBaseImage(BaseImageState state, IfdLevelState level)
    {
        state.Width = level.Width;
        state.Height = level.Height;
        state.BandCount = level.SamplesPerPixel;
        state.TileWidth = level.TileWidth;
        state.TileHeight = level.TileHeight;
        state.Compression = level.Compression;
        state.Predictor = level.Predictor;
        state.PixelType = ClassifyPixelType(state.BitsPerSample, state.SampleFormat);

        ApplyModelTransformation(state);
        ComputeExtent(state);
    }

    private static void ApplyModelTransformation(BaseImageState state)
    {
        if (state.ModelTransformation is not { Length: 16 } matrix)
        {
            return;
        }

        if (matrix.Any(value => !double.IsFinite(value))
            || IsNonZero(matrix[1]) || IsNonZero(matrix[4])
            || IsNonZero(matrix[2]) || IsNonZero(matrix[6])
            || IsNonZero(matrix[12]) || IsNonZero(matrix[13])
            || IsNonZero(matrix[14]) || BitConverter.DoubleToInt64Bits(matrix[15]) != BitConverter.DoubleToInt64Bits(1d)
            || matrix[0] <= 0
            || matrix[5] >= 0)
        {
            throw new InvalidDataException(
                "COG ModelTransformation declares a rotated, sheared, or non-north-up grid, which direct tile serving cannot align safely.");
        }

        state.XMin = matrix[3];
        state.YMax = matrix[7];
        state.PixelScaleX = matrix[0];
        state.PixelScaleY = -matrix[5];
        state.HasPixelScale = true;
    }

    private static void ComputeExtent(BaseImageState state)
    {
        if (state.HasPixelScale && state.Width > 0 && state.Height > 0)
        {
            if (state.ModelTransformation is null)
            {
                state.XMin -= state.TiepointI * state.PixelScaleX;
                state.YMax += state.TiepointJ * state.PixelScaleY;
            }

            if (state.RasterType == 2)
            {
                state.XMin -= state.PixelScaleX / 2d;
                state.YMax += state.PixelScaleY / 2d;
            }

            state.XMax = state.XMin + state.PixelScaleX * state.Width;
            state.YMin = state.YMax - state.PixelScaleY * state.Height;
            if (!double.IsFinite(state.XMin) || !double.IsFinite(state.YMax)
                || !double.IsFinite(state.XMax) || !double.IsFinite(state.YMin)
                || state.PixelScaleX <= 0 || state.PixelScaleY <= 0)
            {
                throw new InvalidDataException("COG georeferencing does not describe a finite north-up grid.");
            }

            return;
        }

        // Exact-zero check is intentional: sentinel defaults are only overwritten in the branch above.
        if (!IsNonZero(state.XMax) && !IsNonZero(state.XMin) && state.Width > 0)
        {
            state.XMax = state.Width;
            state.YMin = 0;
            state.YMax = state.Height;
        }
    }

    private static bool IsNonZero(double value) => (BitConverter.DoubleToInt64Bits(value) & long.MaxValue) != 0;

    private static async Task<(long[] Offsets, int[] ByteCounts)> ReadTileIndexAsync(
        HeaderSliceReader source,
        IfdLevelState level,
        CancellationToken cancellationToken)
    {
        if (!level.TileOffsetsEntry.HasValue || !level.TileByteCountsEntry.HasValue)
        {
            return ([], []);
        }

        var offsets = await ReadLongArrayFromEntryAsync(source, level.TileOffsetsEntry.Value, cancellationToken).ConfigureAwait(false);
        var byteCounts = await ReadIntArrayFromEntryAsync(source, level.TileByteCountsEntry.Value, cancellationToken).ConfigureAwait(false);
        return (offsets, byteCounts);
    }

    private static CogMetadata BuildMetadata(BaseImageState state, List<CogOverviewLevel> overviewLevels, bool isLittleEndian)
    {
        if (state.Width == 0 || state.Height == 0)
        {
            throw new InvalidDataException("COG file does not contain valid image dimensions.");
        }

        return new CogMetadata(
            state.Width,
            state.Height,
            state.BandCount,
            state.PixelType,
            state.Srid,
            TiffConstants.GetCompressionName(state.Compression),
            state.TileWidth,
            state.TileHeight,
            overviewLevels.ToArray(),
            new RasterExtent { XMin = state.XMin, YMin = state.YMin, XMax = state.XMax, YMax = state.YMax, Srid = state.Srid },
            state.BitsPerSample,
            state.Predictor,
            isLittleEndian,
            state.PhotometricInterpretation,
            state.PlanarConfiguration,
            state.NoData);
    }

    private static async Task<long[]> ReadLongArrayFromEntryAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.IsInline)
        {
            return entry.Count == 1
                ? [entry.ValueOrOffset]
                : source.Parser.ReadInlineLongArray(entry.ValueOrOffset, (int)entry.Count, entry.Type);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        return source.Parser.ReadLongArray(data.Span, (int)entry.Count, entry.Type);
    }

    private static async Task<int[]> ReadIntArrayFromEntryAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.IsInline)
        {
            return entry.Count == 1
                ? [(int)entry.ValueOrOffset]
                : source.Parser.ReadInlineIntArray(entry.ValueOrOffset, (int)entry.Count, entry.Type);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        return source.Parser.ReadIntArray(data.Span, (int)entry.Count, entry.Type);
    }

    private static async Task<byte[]> ReadJpegTablesAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.Type is not (TiffConstants.TypeUndefined or TiffConstants.TypeByte)
            || entry.Count is < 4 or > JpegTileAssembler.MaxJpegTablesBytes
            || (!entry.IsInline && entry.ValueOrOffset < 0))
        {
            throw new InvalidDataException(
                $"COG JPEGTables must hold 4-{JpegTileAssembler.MaxJpegTablesBytes} UNDEFINED bytes.");
        }

        var tables = entry.IsInline
            ? source.Parser.ReadInlineIntArray(entry.ValueOrOffset, (int)entry.Count, entry.Type).Select(value => (byte)value).ToArray()
            : (await source.ReadExactAsync(entry.ValueOrOffset, (int)entry.Count, ct).ConfigureAwait(false)).ToArray();
        if (tables.Length != entry.Count || !JpegTileAssembler.IsValidTables(tables))
        {
            throw new InvalidDataException("COG JPEGTables is not a well-formed tables-only JPEG stream.");
        }

        return tables;
    }

    // File-controlled IFD sizes are rejected before they can drive an unbounded range read.
    private static int GetValidatedExternalArrayByteCount(IfdEntry entry)
    {
        if (entry.ValueOrOffset < 0)
        {
            throw new InvalidDataException(
                $"TIFF IFD entry (tag {entry.Tag}) declares a negative external offset {entry.ValueOrOffset}.");
        }

        if (entry.Count is <= 0 or > MaxExternalArrayElements)
        {
            throw new InvalidDataException(
                $"TIFF IFD entry (tag {entry.Tag}) declares {entry.Count} external values, outside the supported range of 1-{MaxExternalArrayElements}.");
        }

        return checked((int)(entry.Count * TiffConstants.GetTypeSize(entry.Type)));
    }

    private static async Task<(int Srid, int RasterType)> ExtractGeoKeysAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.Count < 4 || entry.IsInline)
        {
            return (0, 1);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var geoKeyData = await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false);
        if (geoKeyData.Length < 8)
        {
            throw new InvalidDataException("COG GeoKeyDirectoryTag data is truncated.");
        }

        return ParseGeoKeyDirectory(geoKeyData.Span, source.Parser.IsLittleEndian);
    }

    private static (int Srid, int RasterType) ParseGeoKeyDirectory(ReadOnlySpan<byte> geoKeyData, bool isLittleEndian)
    {
        var numKeys = ReadUInt16(geoKeyData, 6, isLittleEndian);
        var state = new GeoKeyState();

        for (var i = 0; i < numKeys; i++)
        {
            var keyOffset = 8 + i * 8;
            if (keyOffset + 8 > geoKeyData.Length)
            {
                break;
            }

            if (ReadUInt16(geoKeyData, keyOffset + 2, isLittleEndian) != 0)
            {
                continue;
            }

            var keyId = ReadUInt16(geoKeyData, keyOffset, isLittleEndian);
            var value = ReadUInt16(geoKeyData, keyOffset + 6, isLittleEndian);
            ApplyGeoKey(ref state, keyId, value);
        }

        // Prefer projected CRS when present; never fall back to GeographicTypeGeoKey for
        // user-defined projected CRSs (32767) or projected model types (GTModelTypeGeoKey == 1).
        var resolvedSrid = state.ProjectedSrid > 0
            ? state.ProjectedSrid
            : state.HasCustomProjectedCrs || state.IsProjectedModel ? 0 : state.GeographicSrid;
        return (resolvedSrid, state.RasterType);
    }

    private static void ApplyGeoKey(ref GeoKeyState state, ushort keyId, ushort value)
    {
        switch (keyId)
        {
            case TiffConstants.GeoKeyModelTypeGeoKey when value == TiffConstants.ModelTypeProjected:
                state.IsProjectedModel = true;
                break;
            case TiffConstants.GeoKeyProjectedCSTypeGeoKey when value == TiffConstants.GeoKeyUserDefined:
                state.HasCustomProjectedCrs = true;
                break;
            case TiffConstants.GeoKeyProjectedCSTypeGeoKey when value != 0:
                state.ProjectedSrid = value;
                break;
            case TiffConstants.GeoKeyGeographicTypeGeoKey when value != 0 && value != TiffConstants.GeoKeyUserDefined:
                state.GeographicSrid = value;
                break;
            case TiffConstants.GeoKeyRasterTypeGeoKey when value != 0 && value != TiffConstants.GeoKeyUserDefined:
                state.RasterType = value;
                break;
        }
    }

    private static async Task<(double I, double J, double X, double Y)> ExtractTiepointAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.Count < 6 || entry.Type != TiffConstants.TypeDouble)
        {
            return (0, 0, 0, 0);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = (await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false)).Span;
        if (data.Length < 40)
        {
            throw new InvalidDataException("COG ModelTiepointTag data is truncated.");
        }

        var isLittleEndian = source.Parser.IsLittleEndian;
        return (
            ReadDouble(data, 0, isLittleEndian),
            ReadDouble(data, 8, isLittleEndian),
            ReadDouble(data, 24, isLittleEndian),
            ReadDouble(data, 32, isLittleEndian));
    }

    private static async Task<double[]?> ExtractModelTransformationAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.Count != 16 || entry.Type != TiffConstants.TypeDouble || entry.IsInline)
        {
            throw new InvalidDataException(
                "COG ModelTransformationTag must contain sixteen external DOUBLE values.");
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = (await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false)).Span;
        if (data.Length < 16 * sizeof(double))
        {
            throw new InvalidDataException("COG ModelTransformationTag data is truncated.");
        }

        var values = new double[16];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = ReadDouble(data, i * sizeof(double), source.Parser.IsLittleEndian);
        }

        return values;
    }

    private static async Task<(double ScaleX, double ScaleY)> ExtractPixelScaleAsync(
        HeaderSliceReader source,
        IfdEntry entry,
        CancellationToken ct)
    {
        if (entry.Count < 3 || entry.Type != TiffConstants.TypeDouble)
        {
            return (1, 1);
        }

        var totalBytes = GetValidatedExternalArrayByteCount(entry);
        var data = (await source.ReadExactAsync(entry.ValueOrOffset, totalBytes, ct).ConfigureAwait(false)).Span;
        if (data.Length < 16)
        {
            throw new InvalidDataException("COG ModelPixelScaleTag data is truncated.");
        }

        return (
            ReadDouble(data, 0, source.Parser.IsLittleEndian),
            ReadDouble(data, 8, source.Parser.IsLittleEndian));
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
            TiffConstants.SampleFormatUnsigned => "uint",
            _ => throw new InvalidDataException("COG SampleFormat must be unsigned integer, signed integer, or IEEE floating point; complex and undefined samples are unsupported.")
        };
        return $"{prefix}{bitsPerSample}";
    }

    private sealed class HeaderSliceReader(
        ICloudRangeReader reader,
        string bucket,
        string key,
        byte[] header,
        TiffIfdParser parser)
    {
        public TiffIfdParser Parser => parser;

        // Serves any range fully contained in the prefetched header buffer with zero extra cloud I/O.
        // For files shorter than InitialReadSize, the entire file is already in header so we also slice directly.
        public ValueTask<ReadOnlyMemory<byte>> ReadExactAsync(long offset, int length, CancellationToken ct)
        {
            if (offset >= 0 && offset <= header.Length)
            {
                var available = header.Length - (int)offset;
                if (length <= available)
                {
                    return ValueTask.FromResult<ReadOnlyMemory<byte>>(header.AsMemory((int)offset, length));
                }

                if (header.Length < InitialReadSize)
                {
                    return ValueTask.FromResult<ReadOnlyMemory<byte>>(header.AsMemory((int)offset, available));
                }
            }

            return ReadRemoteAsync(offset, length, ct);
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadForIfdAsync(long offset, int length, CancellationToken ct)
            => ReadExactAsync(offset, length, ct);

        private async ValueTask<ReadOnlyMemory<byte>> ReadRemoteAsync(long offset, int length, CancellationToken ct)
            => await reader.ReadRangeAsync(bucket, key, offset, length, ct).ConfigureAwait(false);
    }

    private sealed class BaseImageState
    {
        public int Width;
        public int Height;
        public int BandCount = 1;
        public int TileWidth = 256;
        public int TileHeight = 256;
        public ushort Compression = TiffConstants.CompressionNone;
        public int BitsPerSample = 8;
        public int Predictor = TilePixelLayout.PredictorNone;
        public ushort SampleFormat = TiffConstants.SampleFormatUnsigned;
        public string PixelType = "uint8";
        public int Srid;
        public double XMin;
        public double YMin;
        public double XMax;
        public double YMax;
        public double PixelScaleX;
        public double PixelScaleY;
        public bool HasPixelScale;
        public double[]? ModelTransformation;
        public double TiepointI;
        public double TiepointJ;
        public int RasterType = 1;
        public int PhotometricInterpretation = 1;
        public int PlanarConfiguration = 1;
        public string? NoData;
    }

    private sealed class IfdLevelState
    {
        public int Width;
        public int Height;
        public int TileWidth = 256;
        public int TileHeight = 256;
        public ushort Compression = TiffConstants.CompressionNone;
        public ushort SamplesPerPixel = 1;
        public int Predictor = TilePixelLayout.PredictorNone;
        public IfdEntry? TileOffsetsEntry;
        public IfdEntry? TileByteCountsEntry;
        public byte[]? JpegTables;
    }

    private readonly record struct LevelParseContext(IfdEntry[] Entries, long IfdOffset, int LevelIndex, BaseImageState BaseState);

    private readonly record struct EntryApplyTarget(IfdEntry Entry, IfdLevelState Level, BaseImageState Base, bool IsBaseLevel);

    private readonly record struct BaseEntryTarget(IfdEntry Entry, BaseImageState Base);

    private readonly record struct UniformBandRead(IfdEntry Entry, string MixedErrorMessage);

    private struct GeoKeyState
    {
        public int GeographicSrid;
        public int ProjectedSrid;
        public bool HasCustomProjectedCrs;
        public bool IsProjectedModel;
        public int RasterType;

        public GeoKeyState()
        {
            RasterType = 1;
        }
    }
}

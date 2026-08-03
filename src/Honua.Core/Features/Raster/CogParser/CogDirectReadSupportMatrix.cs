// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Planner disposition for one managed COG direct-read request.
/// </summary>
public enum CogDirectReadDisposition
{
    Direct,
    NoCoverage,
    NeedsPostgisMaterialization,
    NeedsDurableGdal,
    Corrupt,
}

/// <summary>
/// Stable reason for a managed COG direct-read planner decision.
/// </summary>
public enum CogDirectReadReason
{
    Supported,
    OutsideCoverage,
    EmptyTile,
    InvalidStructure,
    InvalidTileInventory,
    InvalidPayload,
    UnsupportedContainer,
    UnsupportedByteOrder,
    UnsupportedLayout,
    UnsupportedCodecLayout,
    UnsupportedOutputEncoding,
    UnsupportedCrs,
    MisalignedGrid,
    EncodedTileTooLarge,
    DecodedTileTooLarge,
}

/// <summary>
/// One human-readable row in the executable managed COG support matrix.
/// </summary>
public sealed record CogDirectReadSupportEntry(
    string Axis,
    string Value,
    bool Supported,
    CogDirectReadDisposition Fallback,
    string Constraint);

/// <summary>
/// Result of planning or validating one managed COG tile read.
/// </summary>
public sealed record CogDirectReadPlan(
    CogDirectReadDisposition Disposition,
    CogDirectReadReason Reason,
    int TileIndex = -1,
    int ExpectedDecodedBytes = 0,
    string? ExpectedContentType = null,
    int ExpectedWidth = 0,
    int ExpectedHeight = 0,
    int ExpectedBandCount = 0)
{
    public bool IsDirect => Disposition == CogDirectReadDisposition.Direct;
}

/// <summary>
/// Authoritative, fixture-backed admission envelope for the pure-managed COG web path.
/// Anything outside this deliberately narrow contract is classified before a tile range
/// read or decode and remains a PostGIS materialization or durable GDAL GP concern.
/// </summary>
public static class CogDirectReadSupportMatrix
{
    private static readonly DecodeCase[] DecodeCases =
    [
        new("NONE", "uint8", 1, 8, 1, [0, 1]),
        new("DEFLATE", "uint8", 1, 8, 1, [0, 1]),
        new("LZW", "uint8", 1, 8, 1, [0, 1]),
        new("LZW", "uint8", 1, 8, 2, [0, 1]),
        new("LZW", "uint16", 1, 16, 1, [0, 1]),
        new("LZW", "uint16", 1, 16, 2, [0, 1]),
        new("LZW", "uint8", 3, 8, 2, [2]),
        new("ZSTD", "uint8", 1, 8, 1, [0, 1]),
        new("ZSTD", "uint16", 1, 16, 2, [0, 1]),
        new("JPEG", "uint8", 1, 8, 1, [0, 1]),
        new("JPEG", "uint8", 3, 8, 1, [2, 6]),
    ];

    /// <summary>
    /// Complete matrix rows used for capability evidence and conformance assertions.
    /// </summary>
    public static IReadOnlyList<CogDirectReadSupportEntry> Entries { get; } = BuildEntries();

    /// <summary>
    /// Evaluates container, layout, decoder, and requested-encoding admission without
    /// resolving a particular WebMercator tile.
    /// </summary>
    public static CogDirectReadPlan EvaluateSource(CogMetadata metadata, RasterFormat format)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (metadata.Width <= 0 || metadata.Height <= 0 ||
            metadata.TileWidth <= 0 || metadata.TileHeight <= 0 ||
            metadata.BandCount <= 0 || metadata.BitsPerSample <= 0 ||
            metadata.OverviewLevels is not { Length: > 0 })
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidStructure);
        }

        if (metadata.IsBigTiff)
        {
            return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.UnsupportedContainer);
        }

        if (!metadata.IsLittleEndian)
        {
            return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.UnsupportedByteOrder);
        }

        if (metadata.TileWidth != metadata.TileHeight ||
            metadata.PlanarConfiguration != TiffConstants.PlanarConfigurationContiguous ||
            metadata.Orientation != TiffConstants.OrientationTopLeft ||
            metadata.HasModelTransformation ||
            metadata.HasSubIfds ||
            metadata.HasHeterogeneousOverviewLayout)
        {
            return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.UnsupportedLayout);
        }

        if (!DecodeCases.Any(candidate => candidate.Matches(metadata)))
        {
            return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.UnsupportedCodecLayout);
        }

        if (string.Equals(metadata.Compression, "JPEG", StringComparison.Ordinal))
        {
            return format switch
            {
                RasterFormat.JPEG => Direct(expectedContentType: "image/jpeg"),
                RasterFormat.PNG or RasterFormat.TIFF or RasterFormat.COG or RasterFormat.Raw =>
                    Reject(
                        CogDirectReadDisposition.NeedsPostgisMaterialization,
                        CogDirectReadReason.UnsupportedOutputEncoding),
                _ => Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.UnsupportedOutputEncoding),
            };
        }

        return format == RasterFormat.Raw
            ? Direct(expectedContentType: "application/octet-stream")
            : Reject(CogDirectReadDisposition.NeedsPostgisMaterialization, CogDirectReadReason.UnsupportedOutputEncoding);
    }

    /// <summary>
    /// Plans one exact tile range after applying the source matrix, CRS/lattice checks,
    /// and complete tile-inventory validation.
    /// </summary>
    public static CogDirectReadPlan PlanTile(
        CogMetadata metadata,
        CogOverviewLevel overview,
        int level,
        int row,
        int col,
        RasterFormat format)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(overview);

        var source = EvaluateSource(metadata, format);
        if (!source.IsDirect)
        {
            return source;
        }

        if (overview.Width <= 0 || overview.Height <= 0)
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidStructure);
        }

        int expectedTileCount;
        try
        {
            var tilesAcross = checked((overview.Width + metadata.TileWidth - 1) / metadata.TileWidth);
            var tilesDown = checked((overview.Height + metadata.TileHeight - 1) / metadata.TileHeight);
            expectedTileCount = checked(tilesAcross * tilesDown);
        }
        catch (OverflowException)
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidTileInventory);
        }

        if (overview.TileOffsets is null ||
            overview.TileByteCounts is null ||
            expectedTileCount <= 0 ||
            overview.TileOffsets.Length != expectedTileCount ||
            overview.TileByteCounts.Length != expectedTileCount)
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidTileInventory);
        }

        var extentWidth = metadata.Extent.XMax - metadata.Extent.XMin;
        var extentHeight = metadata.Extent.YMax - metadata.Extent.YMin;
        if (extentWidth <= 0 || extentHeight <= 0)
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidStructure);
        }

        if (metadata.Srid != 3857)
        {
            return Reject(CogDirectReadDisposition.NeedsPostgisMaterialization, CogDirectReadReason.UnsupportedCrs);
        }

        var tileBounds = TileMath.GetTileBounds(col, row, level);
        if (tileBounds.XMax <= metadata.Extent.XMin || tileBounds.XMin >= metadata.Extent.XMax ||
            tileBounds.YMax <= metadata.Extent.YMin || tileBounds.YMin >= metadata.Extent.YMax)
        {
            return Reject(CogDirectReadDisposition.NoCoverage, CogDirectReadReason.OutsideCoverage);
        }

        if (!TryResolveAlignedTileIndex(metadata, overview, tileBounds, out var tileIndex))
        {
            return Reject(CogDirectReadDisposition.NeedsPostgisMaterialization, CogDirectReadReason.MisalignedGrid);
        }

        if (overview.TileOffsets[tileIndex] <= 0 || overview.TileByteCounts[tileIndex] <= 0)
        {
            return Reject(CogDirectReadDisposition.NoCoverage, CogDirectReadReason.EmptyTile);
        }

        if (overview.TileByteCounts[tileIndex] > TileDecompressor.DefaultMaxDecompressedBytes)
        {
            return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.EncodedTileTooLarge);
        }

        var expectedDecodedBytes = 0;
        if (!string.Equals(metadata.Compression, "JPEG", StringComparison.Ordinal))
        {
            try
            {
                expectedDecodedBytes = checked(
                    metadata.TileWidth * metadata.TileHeight * metadata.BandCount * (metadata.BitsPerSample / 8));
            }
            catch (OverflowException)
            {
                return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.DecodedTileTooLarge);
            }

            if (expectedDecodedBytes <= 0 || expectedDecodedBytes > TileDecompressor.DefaultMaxDecompressedBytes)
            {
                return Reject(CogDirectReadDisposition.NeedsDurableGdal, CogDirectReadReason.DecodedTileTooLarge);
            }

            if (string.Equals(metadata.Compression, "NONE", StringComparison.Ordinal) &&
                overview.TileByteCounts[tileIndex] != expectedDecodedBytes)
            {
                return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidPayload);
            }
        }

        return source with
        {
            TileIndex = tileIndex,
            ExpectedDecodedBytes = expectedDecodedBytes,
            ExpectedWidth = metadata.TileWidth,
            ExpectedHeight = metadata.TileHeight,
            ExpectedBandCount = metadata.BandCount,
        };
    }

    /// <summary>
    /// Validates that decoder output is the exact admitted representation. Raw tiles must
    /// have the declared full-tile byte count; JPEG pass-through must be a standalone
    /// codestream with its own tables, frame, scan, and terminal EOI marker.
    /// </summary>
    public static CogDirectReadPlan ValidatePayload(
        CogDirectReadPlan plan,
        ReadOnlySpan<byte> payload,
        string contentType)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(contentType);

        if (!plan.IsDirect || !string.Equals(plan.ExpectedContentType, contentType, StringComparison.Ordinal))
        {
            return Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidPayload);
        }

        var valid = contentType == "image/jpeg"
            ? IsStandaloneJpeg(payload, plan.ExpectedWidth, plan.ExpectedHeight, plan.ExpectedBandCount)
            : plan.ExpectedDecodedBytes > 0 && payload.Length == plan.ExpectedDecodedBytes;

        return valid ? plan : Reject(CogDirectReadDisposition.Corrupt, CogDirectReadReason.InvalidPayload);
    }

    private static bool TryResolveAlignedTileIndex(
        CogMetadata metadata,
        CogOverviewLevel overview,
        TileBounds tileBounds,
        out int tileIndex)
    {
        tileIndex = -1;
        var overviewPixelWidth = (metadata.Extent.XMax - metadata.Extent.XMin) / overview.Width;
        var overviewPixelHeight = (metadata.Extent.YMax - metadata.Extent.YMin) / overview.Height;

        if (!TryResolvePixelCoordinate((tileBounds.XMin - metadata.Extent.XMin) / overviewPixelWidth, out var minPixelX) ||
            !TryResolvePixelCoordinate((tileBounds.XMax - metadata.Extent.XMin) / overviewPixelWidth, out var maxPixelX) ||
            !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMax) / overviewPixelHeight, out var minPixelY) ||
            !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMin) / overviewPixelHeight, out var maxPixelY))
        {
            return false;
        }

        if (maxPixelX - minPixelX != metadata.TileWidth ||
            maxPixelY - minPixelY != metadata.TileHeight ||
            minPixelX < 0 || minPixelY < 0 ||
            maxPixelX > overview.Width || maxPixelY > overview.Height ||
            minPixelX % metadata.TileWidth != 0 || minPixelY % metadata.TileHeight != 0)
        {
            return false;
        }

        var tilesAcross = (overview.Width + metadata.TileWidth - 1) / metadata.TileWidth;
        var tileX = minPixelX / metadata.TileWidth;
        var tileY = minPixelY / metadata.TileHeight;
        tileIndex = (tileY * tilesAcross) + tileX;
        return tileIndex >= 0 && tileIndex < overview.TileOffsets.Length;
    }

    private static bool TryResolvePixelCoordinate(double value, out int pixelCoordinate)
    {
        const double epsilon = 1e-6;
        var rounded = Math.Round(value);
        if (Math.Abs(value - rounded) > epsilon || rounded < int.MinValue || rounded > int.MaxValue)
        {
            pixelCoordinate = 0;
            return false;
        }

        pixelCoordinate = (int)rounded;
        return true;
    }

    private static bool IsStandaloneJpeg(
        ReadOnlySpan<byte> payload,
        int expectedWidth,
        int expectedHeight,
        int expectedBandCount)
    {
        if (payload.Length < 16 ||
            payload[0] != 0xFF || payload[1] != 0xD8 ||
            payload[^2] != 0xFF || payload[^1] != 0xD9)
        {
            return false;
        }

        var sawFrame = false;
        var sawQuantizationTable = false;
        var sawHuffmanTable = false;
        var offset = 2;

        while (offset < payload.Length - 2)
        {
            if (payload[offset++] != 0xFF)
            {
                return false;
            }

            while (offset < payload.Length && payload[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= payload.Length)
            {
                return false;
            }

            var marker = payload[offset++];
            if (marker == 0xDA)
            {
                if (!sawFrame || !sawQuantizationTable || !sawHuffmanTable ||
                    offset > payload.Length - 4)
                {
                    return false;
                }

                var scanHeaderLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
                var scanComponentCount = payload[offset + 2];
                var scanDataOffset = offset + scanHeaderLength;
                return scanComponentCount is > 0 && scanComponentCount <= expectedBandCount &&
                       scanHeaderLength == 6 + (2 * scanComponentCount) &&
                       scanDataOffset < payload.Length - 2;
            }

            if (offset > payload.Length - 2)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            if (segmentLength < 2 || segmentLength > payload.Length - offset)
            {
                return false;
            }

            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (segmentLength != 8 + (3 * expectedBandCount) ||
                    payload[offset + 2] != 8 ||
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 3, 2)) != expectedHeight ||
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 5, 2)) != expectedWidth ||
                    payload[offset + 7] != expectedBandCount)
                {
                    return false;
                }

                sawFrame = true;
            }
            sawHuffmanTable |= marker == 0xC4 && segmentLength >= 20;
            sawQuantizationTable |= marker == 0xDB && segmentLength >= 67;
            offset += segmentLength;
        }

        return false;
    }

    private static CogDirectReadPlan Direct(string expectedContentType)
        => new(CogDirectReadDisposition.Direct, CogDirectReadReason.Supported, ExpectedContentType: expectedContentType);

    private static CogDirectReadPlan Reject(CogDirectReadDisposition disposition, CogDirectReadReason reason)
        => new(disposition, reason);

    private static ReadOnlyCollection<CogDirectReadSupportEntry> BuildEntries()
    {
        var entries = new List<CogDirectReadSupportEntry>
        {
            new("container", "ClassicTIFF", true, CogDirectReadDisposition.Direct, "Little-endian only; BigTIFF and big-endian require durable GDAL normalization."),
            new("layout", "square tiled, contiguous, top-left, north-up", true, CogDirectReadDisposition.Direct, "No strips, planar-separate, rotation/model transform, SubIFD, heterogeneous overview layout, or tile payload above 128 MiB."),
            new("crs", "EPSG:3857 exact WebMercator lattice", true, CogDirectReadDisposition.Direct, "No managed warp or resampling."),
            new("crs", "other or non-aligned", false, CogDirectReadDisposition.NeedsPostgisMaterialization, "Materialize through the canonical PostGIS-first workflow."),
            new("encoding", "standalone JPEG -> JPEG", true, CogDirectReadDisposition.Direct, "Pass-through only after tables/frame/scan/EOI validation."),
            new("encoding", "lossless decoded samples -> Raw", true, CogDirectReadDisposition.Direct, "Decoded byte count must exactly match the admitted tile layout."),
            new("encoding", "PNG/TIFF/COG or transcoding", false, CogDirectReadDisposition.NeedsPostgisMaterialization, "The web path has no image/COG encoder."),
        };

        foreach (var decodeCase in DecodeCases)
        {
            entries.Add(new CogDirectReadSupportEntry(
                "decode",
                decodeCase.DisplayName,
                true,
                CogDirectReadDisposition.Direct,
                "Fixture-backed exact combination."));
        }

        entries.Add(new CogDirectReadSupportEntry(
            "decode",
            "all other codec/pixel/band/predictor/photometric combinations",
            false,
            CogDirectReadDisposition.NeedsDurableGdal,
            "Normalize in a durable local/AWS Batch GDAL worker; do not add native codecs to web."));

        return entries.AsReadOnly();
    }

    private sealed record DecodeCase(
        string Compression,
        string PixelType,
        int BandCount,
        int BitsPerSample,
        int Predictor,
        int[] PhotometricInterpretations)
    {
        internal bool Matches(CogMetadata metadata)
            => string.Equals(Compression, metadata.Compression, StringComparison.Ordinal) &&
               string.Equals(PixelType, metadata.PixelType, StringComparison.Ordinal) &&
               BandCount == metadata.BandCount &&
               BitsPerSample == metadata.BitsPerSample &&
               Predictor == metadata.Predictor &&
               PhotometricInterpretations.Contains(metadata.PhotometricInterpretation);

        internal string DisplayName =>
            $"{Compression} {PixelType} bands={BandCount} predictor={Predictor} photometric={string.Join('/', PhotometricInterpretations)}";
    }
}

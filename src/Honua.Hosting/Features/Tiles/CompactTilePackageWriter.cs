// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Honua.Infrastructure.Tiles;

/// <summary>
/// Admission limits for uncompressed CompactV2 bundle and package data.
/// </summary>
/// <param name="MaximumBundleBytes">Maximum bytes retained by one in-memory bundle.</param>
/// <param name="MaximumPackageBytes">Maximum aggregate uncompressed bundle bytes admitted to one package.</param>
internal readonly record struct CompactTilePackageLimits(long MaximumBundleBytes, long MaximumPackageBytes)
{
    /// <summary>Default limits suitable for synchronous packaging.</summary>
    public static CompactTilePackageLimits Default { get; } = new(
        MaximumBundleBytes: 64L * 1024L * 1024L,
        MaximumPackageBytes: 1024L * 1024L * 1024L);
}

/// <summary>
/// Writes Esri TPKX 1.0 packages containing Compact Cache V2 bundles.
/// </summary>
/// <remarks>
/// Input is streamed one 128-by-128 bundle at a time. Tiles must therefore be
/// ordered by level, bundle row, and bundle column; order within a bundle is
/// unrestricted.
/// </remarks>
internal static class CompactTilePackageWriter
{
    private const int PacketSize = 128;
    private const int TileCount = PacketSize * PacketSize;
    private const int HeaderSize = 64;
    private const int IndexSize = TileCount * sizeof(ulong);
    private const int BundleDataOffset = HeaderSize + IndexSize;
    private const int FixedBundleOverheadBytes = BundleDataOffset;
    private const double WebMercatorOrigin = 20_037_508.342787;
    private const double ZeroLevelResolution = (WebMercatorOrigin * 2d) / 256d;
    private const int MaximumCompactTileSize = 0xFF_FF_FF;
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] ThumbnailBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    /// <summary>
    /// Writes tiles to a deterministic TPKX archive and returns the tile count.
    /// </summary>
    public static async Task<int> WriteAsync(
        Stream destination,
        string cacheName,
        string tileFormat,
        double[] bounds,
        IAsyncEnumerable<TilePackageWriter.PackagedTile> tiles,
        CancellationToken cancellationToken,
        CompactTilePackageLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(tiles);

        var normalizedTileFormat = NormalizeTileFormat(tileFormat);
        var effectiveLimits = limits ?? CompactTilePackageLimits.Default;
        ValidateLimits(effectiveLimits);
        var levels = new SortedSet<int>();
        var written = 0;
        long admittedPackageBytes = 0;
        BundleKey? activeKey = null;
        MemoryStream? activeBundle = null;
        var maximumTileSize = 0;

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await foreach (var tile in tiles.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            ValidateTile(tile);
            var key = BundleKey.From(tile);
            if (activeKey is not null && key.CompareTo(activeKey.Value) < 0)
            {
                throw new InvalidOperationException("Tiles must be supplied in bundle order (level, bundle row, bundle column).");
            }

            if (activeKey != key)
            {
                // Sparse caches can create many nearly empty bundles. Charge every
                // bundle's fixed header and embedded index before allocating it so
                // package admission cannot be bypassed with sparse coordinates.
                AdmitPackageBytes(ref admittedPackageBytes, FixedBundleOverheadBytes, effectiveLimits);
                if (activeBundle is not null)
                {
                    await WriteBundleEntryAsync(
                        archive,
                        activeKey!.Value,
                        activeBundle,
                        maximumTileSize,
                        cancellationToken).ConfigureAwait(false);
                    await activeBundle.DisposeAsync().ConfigureAwait(false);
                }

                activeKey = key;
                activeBundle = CreateBundle();
                maximumTileSize = 0;
            }

            var recordBytes = sizeof(uint) + tile.Bytes.LongLength;
            if (recordBytes > effectiveLimits.MaximumBundleBytes - activeBundle!.Length)
            {
                throw new InvalidOperationException(
                    $"CompactV2 bundle admission limit of {effectiveLimits.MaximumBundleBytes} bytes would be exceeded.");
            }

            AdmitPackageBytes(ref admittedPackageBytes, recordBytes, effectiveLimits);
            WriteTile(activeBundle!, key, tile);
            maximumTileSize = Math.Max(maximumTileSize, tile.Bytes.Length);
            levels.Add(tile.Level);
            written++;
        }

        if (activeBundle is not null)
        {
            await WriteBundleEntryAsync(
                archive,
                activeKey!.Value,
                activeBundle,
                maximumTileSize,
                cancellationToken).ConfigureAwait(false);
            await activeBundle.DisposeAsync().ConfigureAwait(false);
        }

        if (written == 0)
        {
            throw new InvalidOperationException("A TPKX package requires at least one tile.");
        }

        if (levels.Count < 2)
        {
            throw new InvalidOperationException("A TPKX package requires tiles from at least two levels so minLOD is less than maxLOD.");
        }

        var minimumLevel = levels.Min;
        var maximumLevel = levels.Max;
        var safeName = TilePackageWriter.SanitizeCacheName(cacheName);
        await WriteJsonEntryAsync(
            archive,
            "root.json",
            writer => WriteRootJson(writer, safeName, normalizedTileFormat, bounds, minimumLevel, maximumLevel),
            cancellationToken).ConfigureAwait(false);
        await WriteJsonEntryAsync(
            archive,
            "iteminfo.json",
            writer => WriteItemInfoJson(writer, safeName, bounds),
            cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "thumbnail.png", ThumbnailBytes, CompressionLevel.NoCompression, cancellationToken)
            .ConfigureAwait(false);

        return written;
    }

    private static string NormalizeTileFormat(string tileFormat)
    {
        if (string.IsNullOrWhiteSpace(tileFormat))
        {
            throw new ArgumentException("A supported TPKX tile format is required.", nameof(tileFormat));
        }

        var normalized = tileFormat.Trim().ToUpperInvariant();
        return normalized is "PNG" or "PNG8" or "PNG24" or "PNG32" or "JPEG" or "MIXED"
            ? normalized
            : throw new ArgumentException(
                "The supported TPKX tile formats are PNG, PNG8, PNG24, PNG32, JPEG, and MIXED.",
                nameof(tileFormat));
    }

    private static void ValidateLimits(CompactTilePackageLimits limits)
    {
        if (limits.MaximumBundleBytes < BundleDataOffset + sizeof(uint) + 1L ||
            limits.MaximumBundleBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                $"Maximum bundle bytes must be between {BundleDataOffset + sizeof(uint) + 1L} and {int.MaxValue}.");
        }

        if (limits.MaximumPackageBytes < limits.MaximumBundleBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limits),
                "Maximum package bytes must be greater than or equal to maximum bundle bytes.");
        }
    }

    private static void AdmitPackageBytes(
        ref long admittedPackageBytes,
        long requestedBytes,
        CompactTilePackageLimits limits)
    {
        if (requestedBytes > limits.MaximumPackageBytes - admittedPackageBytes)
        {
            throw new InvalidOperationException(
                $"TPKX package admission limit of {limits.MaximumPackageBytes} uncompressed bundle bytes would be exceeded.");
        }

        admittedPackageBytes += requestedBytes;
    }

    private static void ValidateTile(TilePackageWriter.PackagedTile tile)
    {
        if (tile.Level < 0 || tile.Column < 0 || tile.Row < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tile), "Tile level, row, and column must be non-negative.");
        }

        ArgumentNullException.ThrowIfNull(tile.Bytes);
        if (tile.Bytes.Length == 0)
        {
            throw new ArgumentException("CompactV2 tile payloads cannot be empty because a zero size denotes a missing tile.", nameof(tile));
        }

        if (tile.Bytes.Length > MaximumCompactTileSize)
        {
            throw new ArgumentException("CompactV2 tile payloads cannot exceed the 24-bit index size.", nameof(tile));
        }
    }

    private static MemoryStream CreateBundle()
    {
        var bundle = new MemoryStream(BundleDataOffset + 4096);
        bundle.SetLength(BundleDataOffset);
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], TileCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 5);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..40], 40);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..44], IndexSize + 20);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..56], TileCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..60], 5);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..64], IndexSize);
        bundle.Position = 0;
        bundle.Write(header);
        return bundle;
    }

    private static void WriteTile(
        MemoryStream bundle,
        BundleKey key,
        TilePackageWriter.PackagedTile tile)
    {
        var relativeRow = tile.Row - key.BaseRow;
        var relativeColumn = tile.Column - key.BaseColumn;
        var indexPosition = HeaderSize + (sizeof(ulong) * ((PacketSize * relativeRow) + relativeColumn));
        bundle.Position = indexPosition;
        Span<byte> existing = stackalloc byte[sizeof(ulong)];
        bundle.ReadExactly(existing);
        if (BinaryPrimitives.ReadUInt64LittleEndian(existing) != 0)
        {
            throw new InvalidOperationException("A CompactV2 bundle cannot contain the same tile more than once.");
        }

        bundle.Position = bundle.Length;
        Span<byte> sizePrefix = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sizePrefix, checked((uint)tile.Bytes.Length));
        bundle.Write(sizePrefix);
        var tileOffset = checked((ulong)bundle.Position);
        bundle.Write(tile.Bytes);

        var index = tileOffset | (checked((ulong)tile.Bytes.Length) << 40);
        bundle.Position = indexPosition;
        Span<byte> encodedIndex = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(encodedIndex, index);
        bundle.Write(encodedIndex);
    }

    private static async Task WriteBundleEntryAsync(
        ZipArchive archive,
        BundleKey key,
        MemoryStream bundle,
        int maximumTileSize,
        CancellationToken cancellationToken)
    {
        bundle.Position = 8;
        Span<byte> maximumSize = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(maximumSize, checked((uint)maximumTileSize));
        bundle.Write(maximumSize);
        bundle.Position = 24;
        Span<byte> fileSize = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(fileSize, checked((ulong)bundle.Length));
        bundle.Write(fileSize);
        bundle.Position = 0;

        var path = string.Create(
            CultureInfo.InvariantCulture,
            $"tile/L{key.Level:D2}/R{key.BaseRow:x4}C{key.BaseColumn:x4}.bundle");
        var entry = CreateEntry(archive, path, CompressionLevel.NoCompression);
        await using var stream = entry.Open();
        await bundle.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive,
        string path,
        Action<Utf8JsonWriter> write,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        await WriteEntryAsync(archive, path, buffer.ToArray(), CompressionLevel.Optimal, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string path,
        byte[] bytes,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken)
    {
        var entry = CreateEntry(archive, path, compressionLevel);
        await using var stream = entry.Open();
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static ZipArchiveEntry CreateEntry(ZipArchive archive, string path, CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        entry.LastWriteTime = ArchiveTimestamp;
        return entry;
    }

    private static void WriteRootJson(
        Utf8JsonWriter writer,
        string cacheName,
        string tileFormat,
        double[] bounds,
        int minimumLevel,
        int maximumLevel)
    {
        var extent = ProjectBounds(bounds);
        writer.WriteStartObject();
        writer.WriteNumber("version", 1d);
        writer.WriteString("name", cacheName);
        writer.WriteString("serviceDescription", string.Empty);
        writer.WriteString("tileBundlesPath", "./tile");
        writer.WriteNumber("minLOD", minimumLevel);
        writer.WriteNumber("maxLOD", maximumLevel);
        writer.WriteNumber("minScale", Scale(minimumLevel));
        writer.WriteNumber("maxScale", Scale(maximumLevel));
        writer.WriteString("units", "esriMeters");
        writer.WriteBoolean("resampling", true);
        writer.WriteBoolean("exportTilesAllowed", false);
        WriteSpatialReference(writer, 3857);
        WriteExtent(writer, "initialExtent", extent, 3857);
        WriteExtent(writer, "fullExtent", extent, 3857);

        writer.WriteStartObject("tileImageInfo");
        writer.WriteString("format", tileFormat);
        writer.WriteNumber("compressionQuality", 0);
        writer.WriteEndObject();

        writer.WriteStartObject("storageInfo");
        writer.WriteString("storageFormat", "esriMapCacheStorageModeCompactV2");
        writer.WriteNumber("packetSize", PacketSize);
        writer.WriteEndObject();

        writer.WriteStartObject("tileInfo");
        writer.WriteNumber("rows", 256);
        writer.WriteNumber("cols", 256);
        writer.WriteNumber("dpi", 96);
        writer.WriteString("format", tileFormat);
        writer.WriteStartObject("origin");
        writer.WriteNumber("x", -WebMercatorOrigin);
        writer.WriteNumber("y", WebMercatorOrigin);
        writer.WriteEndObject();
        writer.WriteStartArray("lods");
        for (var level = minimumLevel; level <= maximumLevel; level++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("level", level);
            writer.WriteNumber("resolution", Resolution(level));
            writer.WriteNumber("scale", Scale(level));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        WriteSpatialReference(writer, 3857);
        writer.WriteEndObject();
        writer.WriteStartArray("layers");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteItemInfoJson(Utf8JsonWriter writer, string cacheName, double[] bounds)
    {
        var normalized = NormalizeBounds(bounds);
        writer.WriteStartObject();
        writer.WriteString("id", "00000000000000000000000000000000");
        writer.WriteString("guid", "00000000-0000-0000-0000-000000000000");
        writer.WriteString("name", cacheName);
        writer.WriteString("title", cacheName);
        writer.WriteString("type", "Compact Tile Package");
        writer.WriteNumber("version", 1d);
        writer.WriteString("creator", "Honua");
        writer.WriteNumber("created", 0);
        writer.WriteString("thumbnail", "./thumbnail.png");
        writer.WriteString("snippet", string.Empty);
        writer.WriteString("description", string.Empty);
        writer.WriteString("summary", string.Empty);
        writer.WriteStartArray("typeKeywords");
        writer.WriteStringValue("Compact Tile Package");
        writer.WriteStringValue("Tile Package");
        writer.WriteStringValue("tpkx");
        writer.WriteEndArray();
        writer.WriteStartArray("tags");
        writer.WriteEndArray();
        WriteExtent(writer, "extent", normalized, 4326);
        writer.WriteEndObject();
    }

    private static void WriteSpatialReference(Utf8JsonWriter writer, int wkid)
    {
        writer.WriteStartObject("spatialReference");
        writer.WriteNumber("wkid", wkid);
        writer.WriteNumber("latestWkid", wkid);
        writer.WriteEndObject();
    }

    private static void WriteExtent(Utf8JsonWriter writer, string name, double[] extent, int wkid)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("xmin", extent[0]);
        writer.WriteNumber("ymin", extent[1]);
        writer.WriteNumber("xmax", extent[2]);
        writer.WriteNumber("ymax", extent[3]);
        WriteSpatialReference(writer, wkid);
        writer.WriteEndObject();
    }

    private static double[] ProjectBounds(double[] bounds)
    {
        var normalized = NormalizeBounds(bounds);
        return
        [
            WebMercatorX(normalized[0]),
            WebMercatorY(normalized[1]),
            WebMercatorX(normalized[2]),
            WebMercatorY(normalized[3]),
        ];
    }

    private static double[] NormalizeBounds(double[] bounds)
        => bounds is { Length: 4 }
            ? [bounds[0], bounds[1], bounds[2], bounds[3]]
            : [-180d, -85.05112878d, 180d, 85.05112878d];

    private static double WebMercatorX(double longitude)
        => Math.Clamp(longitude, -180d, 180d) * WebMercatorOrigin / 180d;

    private static double WebMercatorY(double latitude)
    {
        var clamped = Math.Clamp(latitude, -85.05112878d, 85.05112878d);
        return WebMercatorOrigin * Math.Log(Math.Tan(((90d + clamped) * Math.PI) / 360d)) / Math.PI;
    }

    private static double Resolution(int level) => ZeroLevelResolution / Math.Pow(2d, level);

    private static double Scale(int level) => Resolution(level) * 96d * 39.37d;

    private readonly record struct BundleKey(int Level, int BaseRow, int BaseColumn) : IComparable<BundleKey>
    {
        public static BundleKey From(TilePackageWriter.PackagedTile tile)
            => new(tile.Level, tile.Row / PacketSize * PacketSize, tile.Column / PacketSize * PacketSize);

        public int CompareTo(BundleKey other)
        {
            var levelComparison = Level.CompareTo(other.Level);
            if (levelComparison != 0)
            {
                return levelComparison;
            }

            var rowComparison = BaseRow.CompareTo(other.BaseRow);
            return rowComparison != 0 ? rowComparison : BaseColumn.CompareTo(other.BaseColumn);
        }
    }
}

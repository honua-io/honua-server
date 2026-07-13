// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml;
using Honua.Core.Features.TileCachePackage.Abstractions;
using Honua.Core.Features.TileCachePackage.Domain;

namespace Honua.Core.Features.TileCachePackage.Services;

/// <summary>
/// Read-only reader for Esri tile/vector-tile cache packages (#1269).
///
/// Supported layouts (documented Esri cache structure only):
///   * Compact Cache V2 (<c>.tpkx</c> raster, <c>.vtpk</c> vector): a ZIP archive
///     described by <c>root.json</c> whose tiles live in
///     <c>{bundlesPath}/L{zz}/R{rrrr}C{cccc}.bundle</c> files. Each bundle has a
///     64-byte header followed by a 16384-entry (128x128) little-endian index; an
///     index entry's low 40 bits are the tile byte offset and the high 24 bits the
///     tile size. See https://github.com/Esri/raster-tiles-compactcache.
///   * Exploded raster cache (older raster <c>.tpk</c>): loose tile files at
///     <c>_alllayers/L{zz}/R{8hex}/C{8hex}.{png|jpg}</c>, described by
///     <c>conf.xml</c>/<c>conf.cdi</c>.
///
/// Compact Cache V1 (<c>.bundlx</c>/<c>.bundle</c>) is intentionally not handled here;
/// its layout is community-reverse-engineered rather than published, so it is left to
/// a follow-up to stay within the documented-layout guardrail.
/// </summary>
public sealed class EsriTileCachePackageReader : ITileCachePackageReader
{
    // Compact Cache V2 constants (Esri spec).
    private const int CompactV2HeaderSize = 64;
    private const int CompactV2BlockEdge = 128;          // tiles per bundle row/column
    private const int CompactV2IndexEntries = CompactV2BlockEdge * CompactV2BlockEdge; // 16384
    private const int CompactV2IndexSize = CompactV2IndexEntries * 8;                  // 131072
    private const long CompactV2OffsetMask = 0xFF_FFFF_FFFFL; // low 40 bits

    /// <inheritdoc />
    public bool CanRead(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var ext = Path.GetExtension(fileName);
        return ext.Equals(".tpk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tpkx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".vtpk", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<TileCachePackageDescriptor> ReadDescriptorAsync(
        Stream package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var descriptor = ReadDescriptorCore(archive, cancellationToken);
        return Task.FromResult(descriptor);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TileCachePackageTile> ReadTilesAsync(
        Stream package,
        TileCachePackageDescriptor descriptor,
        int minZoom,
        int maxZoom,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(descriptor);

        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);

        IEnumerable<TileCachePackageTile> tiles = descriptor.StorageFormat switch
        {
            TileCacheStorageFormat.CompactV2 => ReadCompactV2Tiles(archive, descriptor, minZoom, maxZoom, cancellationToken),
            TileCacheStorageFormat.Exploded => ReadExplodedTiles(archive, descriptor, minZoom, maxZoom, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported tile-cache storage format: {descriptor.StorageFormat}.")
        };

        foreach (var tile in tiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return tile;
            await Task.Yield();
        }
    }

    private static TileCachePackageDescriptor ReadDescriptorCore(ZipArchive archive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Prefer root.json (.tpkx/.vtpk Compact Cache V2). The .vtpk variant nests it
        // under p12/, the .tpkx variant places it at the archive root.
        var rootEntry = FindEntry(archive, "root.json");
        if (rootEntry != null)
        {
            return ReadCompactV2Descriptor(archive, rootEntry, cancellationToken);
        }

        // Fall back to conf.xml (older exploded raster .tpk).
        var confEntry = FindEntry(archive, "conf.xml");
        if (confEntry != null)
        {
            return ReadExplodedDescriptor(archive, confEntry, cancellationToken);
        }

        throw new InvalidDataException(
            "Tile-cache package does not contain a recognized descriptor (root.json or conf.xml).");
    }

    private static TileCachePackageDescriptor ReadCompactV2Descriptor(
        ZipArchive archive,
        ZipArchiveEntry rootEntry,
        CancellationToken cancellationToken)
    {
        EsriRootJson? root;
        using (var stream = rootEntry.Open())
        {
            root = JsonSerializer.Deserialize(stream, TileCachePackageJsonContext.Default.EsriRootJson);
        }

        if (root?.TileInfo is null)
        {
            throw new InvalidDataException("Tile-cache package root.json is missing tileInfo.");
        }

        var storageFormat = root.StorageInfo?.StorageFormat ?? string.Empty;
        if (!storageFormat.Contains("CompactV2", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Tile-cache package storage format '{storageFormat}' is not supported; only Compact Cache V2 packages (.tpkx/.vtpk) and exploded rasters (.tpk) are read.");
        }

        var format = root.TileInfo.Format ?? string.Empty;
        var (dataType, contentType) = ResolveFormat(format);

        var wkid = root.TileInfo.SpatialReference?.LatestWkid
            ?? root.TileInfo.SpatialReference?.Wkid
            ?? 0;

        var (minLevel, maxLevel) = ResolveLevels(root.TileInfo.Lods);

        // The directory prefix the .bundle files sit under, relative to the archive
        // root. root.json carries it relative to its own location (e.g. "./tile"); we
        // resolve it against the descriptor entry's directory so .vtpk (p12/) and .tpkx
        // (root) both work.
        var bundlesPath = NormalizeBundlesPath(rootEntry.FullName, root.TileBundlesPath ?? "./tile");

        return new TileCachePackageDescriptor
        {
            StorageFormat = TileCacheStorageFormat.CompactV2,
            DataType = dataType,
            ContentType = contentType,
            TileMatrixSetIdentifier = ResolveTileMatrixSet(wkid),
            Wkid = wkid,
            TileSize = root.TileInfo.Cols ?? root.TileInfo.Rows ?? 256,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            Title = string.IsNullOrWhiteSpace(root.Name) ? null : root.Name,
            TileBundlesPath = bundlesPath
        };
    }

    private static TileCachePackageDescriptor ReadExplodedDescriptor(
        ZipArchive archive,
        ZipArchiveEntry confEntry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? format = null;
        int? wkid = null;
        int? tileCols = null;
        var levels = new List<int>();

        using (var stream = confEntry.Open())
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "CacheTileFormat":
                        format = reader.ReadElementContentAsString();
                        break;
                    case "WKID":
                    case "LatestWKID":
                        if (int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedWkid))
                        {
                            wkid = parsedWkid;
                        }

                        break;
                    case "TileCols":
                        if (int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCols))
                        {
                            tileCols = parsedCols;
                        }

                        break;
                    case "LevelID":
                        if (int.TryParse(reader.ReadElementContentAsString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var levelId))
                        {
                            levels.Add(levelId);
                        }

                        break;
                }
            }
        }

        var (dataType, contentType) = ResolveFormat(format ?? "PNG");
        if (dataType != TileCacheDataType.Raster)
        {
            throw new NotSupportedException("Exploded tile caches are raster-only; vector packages must use Compact Cache V2 (.vtpk).");
        }

        var resolvedWkid = wkid ?? 0;
        var minLevel = levels.Count == 0 ? 0 : levels.Min();
        var maxLevel = levels.Count == 0 ? 0 : levels.Max();

        return new TileCachePackageDescriptor
        {
            StorageFormat = TileCacheStorageFormat.Exploded,
            DataType = dataType,
            ContentType = contentType,
            TileMatrixSetIdentifier = ResolveTileMatrixSet(resolvedWkid),
            Wkid = resolvedWkid,
            TileSize = tileCols ?? 256,
            MinLevel = minLevel,
            MaxLevel = maxLevel,
            Title = null,
            TileBundlesPath = string.Empty
        };
    }

    private static IEnumerable<TileCachePackageTile> ReadCompactV2Tiles(
        ZipArchive archive,
        TileCachePackageDescriptor descriptor,
        int minZoom,
        int maxZoom,
        CancellationToken cancellationToken)
    {
        var prefix = descriptor.TileBundlesPath.Length == 0
            ? string.Empty
            : descriptor.TileBundlesPath + "/";

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.FullName.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (prefix.Length != 0 && !entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryParseBundleName(entry.FullName, out var level, out var baseRow, out var baseCol))
            {
                continue;
            }

            if (level < minZoom || level > maxZoom)
            {
                continue;
            }

            // Compact Cache V2 bundles must be read with random access; ZIP entry
            // streams are forward-only, so materialize the (already bounded) bundle.
            byte[] bundle;
            using (var stream = entry.Open())
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bundle = buffer.ToArray();
            }

            foreach (var tile in DecodeCompactV2Bundle(bundle, level, baseRow, baseCol, cancellationToken))
            {
                yield return tile;
            }
        }
    }

    private static IEnumerable<TileCachePackageTile> DecodeCompactV2Bundle(
        byte[] bundle,
        int level,
        int baseRow,
        int baseCol,
        CancellationToken cancellationToken)
    {
        if (bundle.Length < CompactV2HeaderSize + CompactV2IndexSize)
        {
            yield break;
        }

        for (var i = 0; i < CompactV2IndexEntries; i++)
        {
            if ((i & 0x3FF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var indexPos = CompactV2HeaderSize + (i * 8);
            var indexValue = BinaryPrimitives.ReadInt64LittleEndian(bundle.AsSpan(indexPos, 8));
            var offset = indexValue & CompactV2OffsetMask;
            // Use a logical (unsigned) right shift so that tiles whose encoded size has bit 23
            // set (≥ 8 MB) are not sign-extended into a negative Int32, which would cause the
            // size <= 0 guard below to silently skip them.
            var size = (int)((ulong)indexValue >> 40);
            if (size <= 0)
            {
                continue;
            }

            if (offset < 0 || offset + size > bundle.Length)
            {
                continue;
            }

            // Index is row-major within the 128x128 block.
            var rowInBlock = i / CompactV2BlockEdge;
            var colInBlock = i % CompactV2BlockEdge;

            var content = new byte[size];
            Array.Copy(bundle, offset, content, 0, size);

            yield return new TileCachePackageTile
            {
                Z = level,
                X = baseCol + colInBlock,
                Y = baseRow + rowInBlock,
                Content = content
            };
        }
    }

    private static IEnumerable<TileCachePackageTile> ReadExplodedTiles(
        ZipArchive archive,
        TileCachePackageDescriptor descriptor,
        int minZoom,
        int maxZoom,
        CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Length == 0)
            {
                continue;
            }

            if (!TryParseExplodedTilePath(entry.FullName, out var level, out var row, out var col))
            {
                continue;
            }

            if (level < minZoom || level > maxZoom)
            {
                continue;
            }

            byte[] content;
            using (var stream = entry.Open())
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                content = buffer.ToArray();
            }

            yield return new TileCachePackageTile
            {
                Z = level,
                X = col,
                Y = row,
                Content = content
            };
        }
    }

    // -------- helpers --------

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string fileName)
    {
        // The descriptor may sit at the archive root (.tpkx) or under a prefix such
        // as p12/ (.vtpk); take the shallowest match so we anchor bundle resolution
        // at the right directory.
        ZipArchiveEntry? best = null;
        var bestDepth = int.MaxValue;
        foreach (var entry in archive.Entries)
        {
            if (!Path.GetFileName(entry.FullName).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var depth = entry.FullName.Count(c => c == '/');
            if (depth < bestDepth)
            {
                bestDepth = depth;
                best = entry;
            }
        }

        return best;
    }

    private static (TileCacheDataType DataType, string ContentType) ResolveFormat(string format)
    {
        var f = format.Trim();
        if (f.StartsWith("PBF", StringComparison.OrdinalIgnoreCase)
            || f.Contains("VECTOR", StringComparison.OrdinalIgnoreCase)
            || f.Contains("MVT", StringComparison.OrdinalIgnoreCase))
        {
            return (TileCacheDataType.Vector, "application/vnd.mapbox-vector-tile");
        }

        if (f.StartsWith("JPG", StringComparison.OrdinalIgnoreCase)
            || f.StartsWith("JPEG", StringComparison.OrdinalIgnoreCase))
        {
            return (TileCacheDataType.Raster, "image/jpeg");
        }

        // PNG, PNG8/24/32, MIXED (PNG-with-JPEG) all serve as PNG by default.
        return (TileCacheDataType.Raster, "image/png");
    }

    private static (int Min, int Max) ResolveLevels(EsriLod[]? lods)
    {
        if (lods is null || lods.Length == 0)
        {
            return (0, 0);
        }

        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var lod in lods)
        {
            if (lod.Level < min)
            {
                min = lod.Level;
            }

            if (lod.Level > max)
            {
                max = lod.Level;
            }
        }

        return (min, max);
    }

    private static string ResolveTileMatrixSet(int wkid) => wkid switch
    {
        // Geographic WGS84.
        4326 => "WorldCRS84Quad",
        // Web Mercator family (3857 and its aliases) and unknown/missing WKIDs both map to
        // WebMercatorQuad: Esri basemap caches are overwhelmingly 3857 and unknown WKIDs are far
        // more likely Mercator than CRS84. The alias set is no longer enumerated here (#2732); the
        // shared default already covers it.
        _ => "WebMercatorQuad"
    };

    /// <summary>
    /// Resolve the bundle directory prefix relative to the archive root from the
    /// descriptor location and root.json's tileBundlesPath (e.g. "./tile").
    /// </summary>
    internal static string NormalizeBundlesPath(string rootEntryFullName, string tileBundlesPath)
    {
        var rootDir = rootEntryFullName.Contains('/')
            ? rootEntryFullName[..rootEntryFullName.LastIndexOf('/')]
            : string.Empty;

        var relative = tileBundlesPath.Replace('\\', '/').Trim();
        if (relative.StartsWith("./", StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        relative = relative.Trim('/');

        if (rootDir.Length == 0)
        {
            return relative;
        }

        return relative.Length == 0 ? rootDir : $"{rootDir}/{relative}";
    }

    /// <summary>
    /// Parse a Compact Cache V2 bundle path
    /// <c>.../L{zz}/R{rrrr}C{cccc}.bundle</c> into its level and the block base
    /// (top-left) row/column. Hex row/col are the block base (multiples of 128).
    /// </summary>
    internal static bool TryParseBundleName(string fullName, out int level, out int baseRow, out int baseCol)
    {
        level = 0;
        baseRow = 0;
        baseCol = 0;

        var fileName = Path.GetFileNameWithoutExtension(fullName); // R{rrrr}C{cccc}
        var dir = Path.GetFileName(Path.GetDirectoryName(fullName.Replace('/', Path.DirectorySeparatorChar)) ?? string.Empty);

        if (dir.Length < 2 || (dir[0] != 'L' && dir[0] != 'l'))
        {
            return false;
        }

        if (!int.TryParse(dir.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
        {
            return false;
        }

        return TryParseRowCol(fileName, out baseRow, out baseCol);
    }

    private static bool TryParseRowCol(string token, out int row, out int col)
    {
        row = 0;
        col = 0;

        if (token.Length < 3 || (token[0] != 'R' && token[0] != 'r'))
        {
            return false;
        }

        var cIndex = token.IndexOf('C', 1);
        if (cIndex < 0)
        {
            cIndex = token.IndexOf('c', 1);
        }

        if (cIndex <= 1 || cIndex == token.Length - 1)
        {
            return false;
        }

        var rowHex = token.AsSpan(1, cIndex - 1);
        var colHex = token.AsSpan(cIndex + 1);

        return int.TryParse(rowHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out row)
            && int.TryParse(colHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out col);
    }

    /// <summary>
    /// Parse an exploded raster tile path
    /// <c>.../_alllayers/L{zz}/R{8hex}/C{8hex}.{png|jpg}</c> into level, row, col.
    /// </summary>
    internal static bool TryParseExplodedTilePath(string fullName, out int level, out int row, out int col)
    {
        level = 0;
        row = 0;
        col = 0;

        var normalized = fullName.Replace('\\', '/');
        if (!normalized.Contains("_alllayers/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ext = Path.GetExtension(normalized);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        var colToken = Path.GetFileNameWithoutExtension(parts[^1]); // C{8hex}
        var rowToken = parts[^2];                                   // R{8hex}
        var levelToken = parts[^3];                                 // L{zz}

        if (levelToken.Length < 2 || (levelToken[0] != 'L' && levelToken[0] != 'l'))
        {
            return false;
        }

        if (!int.TryParse(levelToken.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
        {
            return false;
        }

        if (rowToken.Length < 2 || (rowToken[0] != 'R' && rowToken[0] != 'r'))
        {
            return false;
        }

        if (colToken.Length < 2 || (colToken[0] != 'C' && colToken[0] != 'c'))
        {
            return false;
        }

        return int.TryParse(rowToken.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out row)
            && int.TryParse(colToken.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out col);
    }
}

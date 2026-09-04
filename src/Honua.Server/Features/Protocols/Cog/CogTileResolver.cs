// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// Resolves tile coordinates to cloud range reads for direct COG tile serving.
/// Uses a three-tier metadata cache: in-memory → database → cloud scan.
/// </summary>
internal sealed class CogTileResolver : ICogTileResolver
{
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(30);
    internal const int MaxCompressedTileBytes = 128 * 1024 * 1024;

    /// <summary>
    /// Builds the memory-cache key for a registration's COG metadata.
    /// Shared with <see cref="CogEndpoints"/> for cache eviction on refresh.
    /// </summary>
    internal static string MetadataCacheKey(long registrationId) => $"cog:metadata:{registrationId}";

    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ICogMetadataReader _metadataReader;
    private readonly ICogStore _cogStore;
    private readonly ILicenseEntitlementService? _licenseEntitlementService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CogTileResolver> _logger;

    public CogTileResolver(
        IEnumerable<ICloudRangeReader> rangeReaders,
        ICogMetadataReader metadataReader,
        ICogStore cogStore,
        IMemoryCache cache,
        ILogger<CogTileResolver> logger,
        ILicenseEntitlementService? licenseEntitlementService = null)
    {
        _rangeReaders = rangeReaders;
        _metadataReader = metadataReader;
        _cogStore = cogStore;
        _licenseEntitlementService = licenseEntitlementService;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RasterResult?> GetTileAsync(
        CogRegistration registration,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default)
    {
        var reader = _rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider);
        if (reader == null)
        {
            throw new InvalidOperationException($"No range reader configured for provider {registration.Provider}.");
        }

        // Pin every metadata/tile read to the current object identity. The registration stores
        // the logical catalog binding, not a mutable object ETag, so a HEAD is required even
        // when the parsed IFD is cached.
        var objectMetadata = await reader.GetObjectMetadataAsync(
            registration.Bucket, registration.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (objectMetadata.SizeBytes <= 0 || string.IsNullOrWhiteSpace(objectMetadata.ETag))
        {
            CogLog.MetadataScanFailed(
                _logger,
                new InvalidDataException("COG object has no usable ETag or size for a pinned tile read."),
                registration.Id);
            return null;
        }

        // Tier 1: In-memory metadata cache
        var cacheKey = MetadataCacheKey(registration.Id);
        var (metadata, metadataSource) = await GetOrLoadMetadataAsync(
            cacheKey, registration, reader, objectMetadata.ETag, cancellationToken).ConfigureAwait(false);

        // Skip COGs with unsupported compression rather than throwing.
        // The foreach loop in GetTileForLayerAsync will try the next COG.
        if (!TileDecompressor.IsSupported(metadata.Compression))
        {
            CogLog.UnsupportedCompression(_logger, metadata.Compression, registration.Id);
            return null;
        }

        // Find the best overview level for the requested zoom
        var overviewLevel = FindBestOverviewLevel(metadata, level);
        if (overviewLevel == null)
        {
            CogLog.CogTileNotFound(_logger, registration.Id, level, row, col);
            return null;
        }

        if (!TryResolveAlignedTileIndex(metadata, overviewLevel, level, row, col, out var tileIndex) ||
            tileIndex < 0 || tileIndex >= overviewLevel.TileOffsets.Length ||
            tileIndex >= overviewLevel.TileByteCounts.Length)
        {
            CogLog.CogTileNotFound(_logger, registration.Id, level, row, col);
            return null;
        }

        var offset = overviewLevel.TileOffsets[tileIndex];
        var length = overviewLevel.TileByteCounts[tileIndex];

        if (offset <= 0 || length <= 0)
        {
            return null; // Empty tile
        }

        if (length > MaxCompressedTileBytes
            || offset > objectMetadata.SizeBytes
            || length > objectMetadata.SizeBytes - offset)
        {
            CogLog.CogTileNotFound(_logger, registration.Id, level, row, col);
            return null;
        }

        // Single range read for the tile data
        var tileData = await reader.ReadRangeAsync(
            registration.Bucket, registration.ObjectKey,
            offset, length, objectMetadata.ETag,
            cancellationToken).ConfigureAwait(false);
        if (tileData.Length != length)
        {
            return null;
        }

        // Decompress based on compression type, reversing the tile's predictor when it declares one.
        var layout = new TilePixelLayout(
            metadata.TileWidth,
            metadata.BandCount,
            metadata.BitsPerSample,
            metadata.Predictor,
            metadata.IsLittleEndian);
        var (decompressedData, contentType) = TileDecompressor.Decompress(tileData, metadata.Compression, layout);

        if (!CanServeRequestedFormat(format, contentType))
        {
            CogLog.UnsupportedTileFormat(_logger, registration.Id, FormatName(format), contentType);
            return null;
        }

        CogLog.CogTileServed(_logger, registration.Id, level, row, col, decompressedData.Length, metadataSource);

        return new RasterResult
        {
            Data = decompressedData,
            ContentType = contentType,
            Width = metadata.TileWidth,
            Height = metadata.TileHeight,
            Srid = metadata.Srid
        };
    }

    /// <inheritdoc />
    public async Task<CogTileLookup> GetTileForLayerAsync(
        int publicationLayerIndex,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default)
    {
        var cogs = await _cogStore.ListByLayerAsync(publicationLayerIndex, cancellationToken).ConfigureAwait(false);
        if (cogs.Length == 0)
        {
            return new CogTileLookup(null, false);
        }

        // Entitlement gate: direct cloud COG serving is a paid runtime capability.
        if (_licenseEntitlementService != null)
        {
            var decision = _licenseEntitlementService.CheckEntitlement("raster.cloud-cog-serving");
            if (!decision.IsActive)
            {
                return new CogTileLookup(null, true);
            }
        }

        foreach (var cog in cogs)
        {
            try
            {
                var tile = await GetTileAsync(cog, level, row, col, format, cancellationToken).ConfigureAwait(false);
                if (tile != null)
                {
                    return new CogTileLookup(tile, false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional catch-all: this is a per-COG loop scanning a mosaic for a tile; one
            // COG's metadata/tile read failure must not abort the scan of the remaining COGs.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                CogLog.MetadataScanFailed(_logger, ex, cog.Id);
            }
        }

        return new CogTileLookup(null, false);
    }

    private async Task<(CogMetadata Metadata, string Source)> GetOrLoadMetadataAsync(
        string cacheKey,
        CogRegistration registration,
        ICloudRangeReader reader,
        string expectedETag,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out CachedCogMetadata? cached)
            && cached is not null
            && string.Equals(cached.ETag, expectedETag, StringComparison.Ordinal))
        {
            return (cached.Metadata, "memory");
        }

        // Scan the current object: persisted summaries have no pinned identity or tile offsets.
        var metadata = await _metadataReader.ReadMetadataAsync(
            new CogPinnedRangeReader(reader, expectedETag),
            registration.Bucket,
            registration.ObjectKey,
            cancellationToken).ConfigureAwait(false);

        // Persist to database
        await _cogStore.UpdateMetadataAsync(registration.Id, metadata, ifdCache: null, cancellationToken).ConfigureAwait(false);

        _cache.Set(cacheKey, new CachedCogMetadata(metadata, expectedETag), new MemoryCacheEntryOptions
        {
            SlidingExpiration = MetadataCacheDuration
        });

        return (metadata, "cloud");
    }

    private static CogOverviewLevel? FindBestOverviewLevel(CogMetadata metadata, int requestedLevel)
    {
        if (metadata.OverviewLevels.Length == 0)
        {
            return null;
        }

        var extentWidth = metadata.Extent.XMax - metadata.Extent.XMin;
        var extentHeight = metadata.Extent.YMax - metadata.Extent.YMin;
        if (extentWidth <= 0 || extentHeight <= 0 || metadata.TileWidth <= 0 || metadata.TileHeight <= 0)
        {
            return metadata.OverviewLevels[0];
        }

        var requestedBounds = TileMath.GetTileBounds(0, 0, requestedLevel);
        var tileSpanX = requestedBounds.XMax - requestedBounds.XMin;
        var tileSpanY = requestedBounds.YMax - requestedBounds.YMin;
        if (tileSpanX <= 0 || tileSpanY <= 0)
        {
            return metadata.OverviewLevels[0];
        }

        // Score each overview by its ground sample distance against the requested tile geometry
        // using the shared selector so the COG and PostGIS-pyramid read paths rank levels
        // identically (#1836).
        var candidateResolutions = new (double ResolutionX, double ResolutionY)[metadata.OverviewLevels.Length];
        for (var i = 0; i < metadata.OverviewLevels.Length; i++)
        {
            var overview = metadata.OverviewLevels[i];
            candidateResolutions[i] = overview.Width > 0 && overview.Height > 0
                ? (extentWidth / overview.Width, extentHeight / overview.Height)
                : (0d, 0d);
        }

        var bestIndex = OverviewLevelSelector.SelectBestIndex(
            tileSpanX, tileSpanY, metadata.TileWidth, metadata.TileHeight, candidateResolutions);

        return bestIndex >= 0 ? metadata.OverviewLevels[bestIndex] : metadata.OverviewLevels[0];
    }

    private static bool TryResolveAlignedTileIndex(
        CogMetadata metadata,
        CogOverviewLevel overviewLevel,
        int level,
        int row,
        int col,
        out int tileIndex)
    {
        tileIndex = -1;

        if (metadata.Srid != 3857)
        {
            return false;
        }

        var extentWidth = metadata.Extent.XMax - metadata.Extent.XMin;
        var extentHeight = metadata.Extent.YMax - metadata.Extent.YMin;
        if (extentWidth <= 0 || extentHeight <= 0 || overviewLevel.Width <= 0 || overviewLevel.Height <= 0)
        {
            return false;
        }

        var tileBounds = TileMath.GetTileBounds(col, row, level);
        var overviewPixelWidth = extentWidth / overviewLevel.Width;
        var overviewPixelHeight = extentHeight / overviewLevel.Height;

        if (!TryResolvePixelCoordinate((tileBounds.XMin - metadata.Extent.XMin) / overviewPixelWidth, out var minPixelX) ||
            !TryResolvePixelCoordinate((tileBounds.XMax - metadata.Extent.XMin) / overviewPixelWidth, out var maxPixelX) ||
            !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMax) / overviewPixelHeight, out var minPixelY) ||
            !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMin) / overviewPixelHeight, out var maxPixelY))
        {
            return false;
        }

        if (maxPixelX - minPixelX != metadata.TileWidth ||
            maxPixelY - minPixelY != metadata.TileHeight ||
            minPixelX < 0 ||
            minPixelY < 0 ||
            maxPixelX > overviewLevel.Width ||
            maxPixelY > overviewLevel.Height ||
            minPixelX % metadata.TileWidth != 0 ||
            minPixelY % metadata.TileHeight != 0)
        {
            return false;
        }

        var tileX = minPixelX / metadata.TileWidth;
        var tileY = minPixelY / metadata.TileHeight;
        var tilesAcross = (overviewLevel.Width + metadata.TileWidth - 1) / metadata.TileWidth;
        var tilesDown = (overviewLevel.Height + metadata.TileHeight - 1) / metadata.TileHeight;
        if (tileX < 0 || tileX >= tilesAcross || tileY < 0 || tileY >= tilesDown)
        {
            return false;
        }

        tileIndex = (tileY * tilesAcross) + tileX;
        return true;
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

    private static bool CanServeRequestedFormat(RasterFormat requestedFormat, string contentType) => requestedFormat switch
    {
        RasterFormat.PNG => contentType == "image/png",
        RasterFormat.JPEG => contentType == "image/jpeg",
        RasterFormat.TIFF or RasterFormat.COG => contentType == "image/tiff",
        RasterFormat.Raw => contentType == "application/octet-stream",
        _ => false
    };

    private static string FormatName(RasterFormat format) => format switch
    {
        RasterFormat.PNG => "PNG",
        RasterFormat.JPEG => "JPEG",
        RasterFormat.TIFF => "TIFF",
        RasterFormat.Raw => "Raw",
        RasterFormat.COG => "COG",
        _ => "Unknown"
    };

    private sealed record CachedCogMetadata(CogMetadata Metadata, string ETag);

}

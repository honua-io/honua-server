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
        RasterFormat format, // Not yet used — tiles are served in native COG compression (JPEG passthrough / DEFLATE).
        CancellationToken cancellationToken = default)
    {
        var reader = _rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider);
        if (reader == null)
        {
            throw new InvalidOperationException($"No range reader configured for provider {registration.Provider}.");
        }

        // Tier 1: In-memory metadata cache
        var cacheKey = MetadataCacheKey(registration.Id);
        var (metadata, metadataSource) = await GetOrLoadMetadataAsync(cacheKey, registration, reader, cancellationToken).ConfigureAwait(false);

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

        if (offset == 0 || length == 0)
        {
            return null; // Empty tile
        }

        // Single range read for the tile data
        var tileData = await reader.ReadRangeAsync(
            registration.Bucket, registration.ObjectKey,
            offset, length,
            cancellationToken).ConfigureAwait(false);

        // Decompress based on compression type
        var (decompressedData, contentType) = TileDecompressor.Decompress(tileData, metadata.Compression);

        if (!CanServeRequestedFormat(format, contentType))
        {
            var requestedFormat = format switch
            {
                RasterFormat.PNG => "PNG",
                RasterFormat.JPEG => "JPEG",
                RasterFormat.TIFF => "TIFF",
                RasterFormat.Raw => "Raw",
                RasterFormat.COG => "COG",
                _ => "Unknown"
            };
            CogLog.UnsupportedTileFormat(_logger, registration.Id, requestedFormat, contentType);
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
        int layerId,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default)
    {
        var cogs = await _cogStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
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
            catch (Exception ex)
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
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out CogMetadata? cached) && cached != null)
        {
            return (cached, "memory");
        }

        // Tier 2: Database cache — only use if overview levels have tile offsets.
        // PHASE-1: The DB currently stores overview summaries (level, width, height)
        // but not tile offsets; ifd_cache is always null. This tier activates once
        // IFD cache serialization is implemented (entries with empty TileOffsets
        // fall through to cloud scan).
        if (registration.Metadata is { OverviewLevels.Length: > 0 } &&
            registration.Metadata.OverviewLevels[0].TileOffsets.Length > 0)
        {
            _cache.Set(cacheKey, registration.Metadata, new MemoryCacheEntryOptions
            {
                SlidingExpiration = MetadataCacheDuration
            });
            return (registration.Metadata, "database");
        }

        // Tier 3: Cloud scan
        var metadata = await _metadataReader.ReadMetadataAsync(
            reader, registration.Bucket, registration.ObjectKey, cancellationToken).ConfigureAwait(false);

        // Persist to database
        await _cogStore.UpdateMetadataAsync(registration.Id, metadata, ifdCache: null, cancellationToken).ConfigureAwait(false);

        _cache.Set(cacheKey, metadata, new MemoryCacheEntryOptions
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
        var requestedPixelWidth = (requestedBounds.XMax - requestedBounds.XMin) / metadata.TileWidth;
        var requestedPixelHeight = (requestedBounds.YMax - requestedBounds.YMin) / metadata.TileHeight;
        if (requestedPixelWidth <= 0 || requestedPixelHeight <= 0)
        {
            return metadata.OverviewLevels[0];
        }

        CogOverviewLevel? bestMatch = null;
        var bestScore = double.MaxValue;

        foreach (var overview in metadata.OverviewLevels)
        {
            if (overview.Width <= 0 || overview.Height <= 0)
            {
                continue;
            }

            var overviewPixelWidth = extentWidth / overview.Width;
            var overviewPixelHeight = extentHeight / overview.Height;
            var widthScore = Math.Abs(((requestedBounds.XMax - requestedBounds.XMin) / overviewPixelWidth) - metadata.TileWidth);
            var heightScore = Math.Abs(((requestedBounds.YMax - requestedBounds.YMin) / overviewPixelHeight) - metadata.TileHeight);
            var score = widthScore + heightScore;

            if (score < bestScore)
            {
                bestScore = score;
                bestMatch = overview;

                if (score <= 1e-6)
                {
                    break;
                }
            }
        }

        return bestMatch ?? metadata.OverviewLevels[0];
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
}

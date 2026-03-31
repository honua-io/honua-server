// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.CloudCog;

/// <summary>
/// Resolves tile coordinates to cloud range reads for direct COG tile serving.
/// Uses a three-tier metadata cache: in-memory → database → cloud scan.
/// </summary>
internal sealed class CloudCogTileResolver : ICloudCogTileResolver
{
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds the memory-cache key for a registration's COG metadata.
    /// Shared with <see cref="CloudCogEndpoints"/> for cache eviction on refresh.
    /// </summary>
    internal static string MetadataCacheKey(long registrationId) => $"cog:metadata:{registrationId}";

    private readonly IEnumerable<ICloudRangeReader> _rangeReaders;
    private readonly ICogMetadataReader _metadataReader;
    private readonly ICloudCogStore _cogStore;
    private readonly ILicenseStatusProvider? _licenseStatusProvider;
    private readonly IMemoryCache _cache;
    private readonly ILogger<CloudCogTileResolver> _logger;

    public CloudCogTileResolver(
        IEnumerable<ICloudRangeReader> rangeReaders,
        ICogMetadataReader metadataReader,
        ICloudCogStore cogStore,
        IMemoryCache cache,
        ILogger<CloudCogTileResolver> logger,
        ILicenseStatusProvider? licenseStatusProvider = null)
    {
        _rangeReaders = rangeReaders;
        _metadataReader = metadataReader;
        _cogStore = cogStore;
        _licenseStatusProvider = licenseStatusProvider;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RasterResult?> GetTileAsync(
        CloudCogRegistration registration,
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
            CloudCogLog.UnsupportedCompression(_logger, metadata.Compression, registration.Id);
            return null;
        }

        // Find the best overview level for the requested zoom
        var overviewLevel = FindBestOverviewLevel(metadata, level);
        if (overviewLevel == null)
        {
            CloudCogLog.CloudTileNotFound(_logger, registration.Id, level, row, col);
            return null;
        }

        // Calculate tile index within this IFD.
        // TODO: This maps web tile row/col directly to COG tile indices, which is
        // only correct for COGs whose internal tiling aligns with the web mercator
        // tile grid. Non-aligned COGs need extent-to-pixel coordinate transformation
        // (web tile → geographic bounds → COG pixel coords → tile index).
        var tilesAcross = (overviewLevel.Width + metadata.TileWidth - 1) / metadata.TileWidth;
        var tilesDown = (overviewLevel.Height + metadata.TileHeight - 1) / metadata.TileHeight;
        var tileIndex = row * tilesAcross + col;

        if (tileIndex < 0 || tileIndex >= overviewLevel.TileOffsets.Length ||
            row >= tilesDown || col >= tilesAcross)
        {
            CloudCogLog.CloudTileNotFound(_logger, registration.Id, level, row, col);
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

        CloudCogLog.CloudTileServed(_logger, registration.Id, level, row, col, decompressedData.Length, metadataSource);

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
    public async Task<CloudCogTileLookup> GetTileForLayerAsync(
        int layerId,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default)
    {
        var cloudCogs = await _cogStore.ListByLayerAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (cloudCogs.Length == 0)
        {
            return new CloudCogTileLookup(null, false);
        }

        // Edition gate: cloud COG serving requires Pro
        if (_licenseStatusProvider != null)
        {
            var license = _licenseStatusProvider.GetCurrentStatus();
            if (license.Edition < HonuaEdition.Pro)
            {
                return new CloudCogTileLookup(null, true);
            }
        }

        foreach (var cog in cloudCogs)
        {
            try
            {
                var tile = await GetTileAsync(cog, level, row, col, format, cancellationToken).ConfigureAwait(false);
                if (tile != null)
                {
                    return new CloudCogTileLookup(tile, false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CloudCogLog.MetadataScanFailed(_logger, ex, cog.Id);
            }
        }

        return new CloudCogTileLookup(null, false);
    }

    private async Task<(CogMetadata Metadata, string Source)> GetOrLoadMetadataAsync(
        string cacheKey,
        CloudCogRegistration registration,
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

        // COG IFD chain: level 0 = full resolution, level N = smallest overview.
        // Web tile zoom: level 0 = world overview (low detail), higher = more detail.
        // Strategy: each COG overview is roughly half the resolution of the previous.
        // Match the requested zoom to the overview whose resolution is closest,
        // using the ratio between full-res width and each overview's width.
        var fullWidth = metadata.OverviewLevels[0].Width;
        if (fullWidth <= 0)
        {
            return metadata.OverviewLevels[0];
        }

        // The maximum zoom level this COG can serve at full resolution.
        // Each overview level halves resolution, so the number of overview IFDs
        // tells us how many zoom levels below full-res we can serve.
        var maxZoom = metadata.OverviewLevels.Length - 1;

        // Map: high web zoom → IFD 0 (full res), low web zoom → last IFD (smallest).
        // Clamp so that zoom levels beyond the COG's range get the best available.
        var ifdIndex = Math.Clamp(maxZoom - requestedLevel, 0, maxZoom);

        return metadata.OverviewLevels[ifdIndex];
    }
}

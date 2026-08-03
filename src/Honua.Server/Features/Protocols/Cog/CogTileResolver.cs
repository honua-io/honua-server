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
        RasterFormat format,
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

        // Find the best overview level for the requested zoom
        var overviewLevel = FindBestOverviewLevel(metadata, level);
        if (overviewLevel == null)
        {
            CogLog.CogTileNotFound(_logger, registration.Id, level, row, col);
            return null;
        }

        var directPlan = CogDirectReadSupportMatrix.PlanTile(
            metadata,
            overviewLevel,
            level,
            row,
            col,
            format);
        if (!directPlan.IsDirect)
        {
            CogLog.DirectReadRejected(
                _logger,
                registration.Id,
                directPlan.Disposition.ToString(),
                directPlan.Reason.ToString());
            return null;
        }

        var offset = overviewLevel.TileOffsets[directPlan.TileIndex];
        var length = overviewLevel.TileByteCounts[directPlan.TileIndex];

        // Single range read for the tile data
        var tileData = await reader.ReadRangeAsync(
            registration.Bucket, registration.ObjectKey,
            offset, length,
            cancellationToken).ConfigureAwait(false);

        // Decompress based on compression type, reversing the tile's predictor when it declares one.
        var layout = new TilePixelLayout(
            metadata.TileWidth,
            metadata.BandCount,
            metadata.BitsPerSample,
            metadata.Predictor,
            metadata.IsLittleEndian);
        var maxDecodedBytes = directPlan.ExpectedDecodedBytes > 0
            ? directPlan.ExpectedDecodedBytes
            : TileDecompressor.DefaultMaxDecompressedBytes;
        var (decompressedData, contentType) = TileDecompressor.Decompress(
            tileData,
            metadata.Compression,
            layout,
            maxDecodedBytes);

        var validatedPlan = CogDirectReadSupportMatrix.ValidatePayload(directPlan, decompressedData, contentType);
        if (!validatedPlan.IsDirect)
        {
            CogLog.DirectReadRejected(
                _logger,
                registration.Id,
                validatedPlan.Disposition.ToString(),
                validatedPlan.Reason.ToString());
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

}

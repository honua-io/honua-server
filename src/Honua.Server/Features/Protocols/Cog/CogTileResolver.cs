// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Protocols.Cog;

internal sealed class CogTileResolver : ICogTileResolver
{
    private static readonly TimeSpan MetadataCacheDuration = TimeSpan.FromMinutes(30);
    internal const int MaxCompressedTileBytes = 128 * 1024 * 1024;

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

    public async Task<RasterResult?> GetTileAsync(
        CogRegistration registration,
        int level,
        int row,
        int col,
        RasterFormat format,
        CancellationToken cancellationToken = default)
    {
        var reader = ResolveRangeReader(registration);
        var request = new TileRequest(registration, new TileIndex(level, col, row), format);
        var cacheKey = MetadataCacheKey(registration.Id);

        // Warm-cache fast path: issue the ETag-conditional range GET directly without a redundant
        // HEAD round-trip. If the object changed (HTTP 412), evict and re-scan once.
        if (_cache.TryGetValue(cacheKey, out CachedCogMetadata? cached) && cached is not null)
        {
            try
            {
                return await ServeTileWithPinnedMetadataAsync(reader, new PinnedServeRequest(request, cached, "memory"), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CloudFileRevisionChangedException)
            {
                _cache.Remove(cacheKey);
            }
        }

        var loaded = await LoadAndCacheMetadataAsync(reader, new MetadataLoadRequest(cacheKey, registration), cancellationToken)
            .ConfigureAwait(false);
        if (loaded is null)
        {
            return null;
        }

        return await ServeTileWithPinnedMetadataAsync(reader, new PinnedServeRequest(request, loaded, "cloud"), cancellationToken)
            .ConfigureAwait(false);
    }

    private ICloudRangeReader ResolveRangeReader(CogRegistration registration)
        => _rangeReaders.FirstOrDefault(r => r.Provider == registration.Provider)
           ?? throw new InvalidOperationException($"No range reader configured for provider {registration.Provider}.");

    private bool IsCloudCogEntitled()
        => _licenseEntitlementService?.CheckEntitlement("raster.cloud-cog-serving").IsActive ?? true;

    private async Task<RasterResult?> TryGetTileSafeAsync(TileRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await GetTileAsync(request.Registration, request.Tile.Z, request.Tile.Y, request.Tile.X, request.Format, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentional catch-all: scanning a mosaic for a tile must not abort remaining COGs when one fails.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CogLog.MetadataScanFailed(_logger, ex, request.Registration.Id);
            return null;
        }
    }

    private async Task<CachedCogMetadata?> LoadAndCacheMetadataAsync(
        ICloudRangeReader reader,
        MetadataLoadRequest load,
        CancellationToken cancellationToken)
    {
        var objectMetadata = await reader.GetObjectMetadataAsync(
            load.Registration.Bucket, load.Registration.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (objectMetadata.SizeBytes <= 0 || string.IsNullOrWhiteSpace(objectMetadata.ETag))
        {
            CogLog.MetadataScanFailed(
                _logger,
                new InvalidDataException("COG object has no usable ETag or size for a pinned tile read."),
                load.Registration.Id);
            return null;
        }

        var metadata = await _metadataReader.ReadMetadataAsync(
            new CogPinnedRangeReader(reader, objectMetadata.ETag),
            load.Registration.Bucket,
            load.Registration.ObjectKey,
            cancellationToken).ConfigureAwait(false);

        await _cogStore.UpdateMetadataAsync(load.Registration.Id, metadata, ifdCache: null, cancellationToken).ConfigureAwait(false);

        var entry = new CachedCogMetadata(metadata, objectMetadata.ETag, objectMetadata.SizeBytes);
        _cache.Set(load.CacheKey, entry, new MemoryCacheEntryOptions { SlidingExpiration = MetadataCacheDuration });
        return entry;
    }

    private async Task<RasterResult?> ServeTileWithPinnedMetadataAsync(
        ICloudRangeReader reader,
        PinnedServeRequest serve,
        CancellationToken cancellationToken)
    {
        var metadata = serve.Pinned.Metadata;
        if (!TileDecompressor.IsSupported(metadata.Compression))
        {
            CogLog.UnsupportedCompression(_logger, metadata.Compression, serve.Request.Registration.Id);
            return null;
        }

        if (ResolveTileSource(metadata, serve.Request.Tile) is not { } source)
        {
            CogLog.CogTileNotFound(_logger, serve.Request.Registration.Id, serve.Request.Tile.Z, serve.Request.Tile.Y, serve.Request.Tile.X);
            return null;
        }

        var tileData = await ReadValidatedTileBytesAsync(reader, new TileReadSpec(serve.Request, serve.Pinned, source), cancellationToken)
            .ConfigureAwait(false);
        if (tileData is null)
        {
            return null;
        }

        return DecodeAndFormatTile(serve, source, tileData);
    }

    private async Task<byte[]?> ReadValidatedTileBytesAsync(
        ICloudRangeReader reader,
        TileReadSpec spec,
        CancellationToken cancellationToken)
    {
        var offset = spec.Source.Overview.TileOffsets[spec.Source.TileIndex];
        var length = spec.Source.Overview.TileByteCounts[spec.Source.TileIndex];
        if (offset <= 0 || length <= 0)
        {
            return null;
        }

        if (length > MaxCompressedTileBytes || offset > spec.Pinned.ObjectSizeBytes || length > spec.Pinned.ObjectSizeBytes - offset)
        {
            CogLog.CogTileNotFound(_logger, spec.Request.Registration.Id, spec.Request.Tile.Z, spec.Request.Tile.Y, spec.Request.Tile.X);
            return null;
        }

        var tileData = await reader.ReadRangeAsync(
            spec.Request.Registration.Bucket,
            spec.Request.Registration.ObjectKey,
            offset,
            length,
            spec.Pinned.ETag,
            cancellationToken).ConfigureAwait(false);
        return tileData.Length == length ? tileData : null;
    }

    private RasterResult? DecodeAndFormatTile(PinnedServeRequest serve, CogTileSource source, byte[] tileData)
    {
        var request = serve.Request;
        var metadata = serve.Pinned.Metadata;
        var assembled = AssembleJpegIfNeeded(serve, source, tileData);
        if (assembled is null)
        {
            return null;
        }

        var predictor = source.Overview.Predictor > 0 ? source.Overview.Predictor : metadata.Predictor;
        var layout = new TilePixelLayout(metadata.TileWidth, metadata.BandCount, metadata.BitsPerSample, predictor, metadata.IsLittleEndian);
        var (decompressedData, contentType) = TileDecompressor.Decompress(assembled, metadata.Compression, layout);
        if (source.Quadrant is { } quadrant)
        {
            decompressedData = CogTileOverzoom.Upsample(decompressedData, metadata, quadrant);
        }

        (decompressedData, contentType) = EncodeRequestedFormat(decompressedData, contentType, serve);
        if (!CanServeRequestedFormat(request.Format, contentType))
        {
            var requestedFormat = FormatName(request.Format);
            CogLog.UnsupportedTileFormat(_logger, request.Registration.Id, requestedFormat, contentType);
            return null;
        }

        CogLog.CogTileServed(_logger, request.Registration.Id, request.Tile.Z, request.Tile.Y, request.Tile.X, decompressedData.Length, serve.MetadataSource);
        return new RasterResult
        {
            Data = decompressedData,
            ContentType = contentType,
            Width = metadata.TileWidth,
            Height = metadata.TileHeight,
            Srid = metadata.Srid
        };
    }

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

        if (!IsCloudCogEntitled())
        {
            return new CogTileLookup(null, true);
        }

        var tileBounds = TileMath.GetTileBounds(col, row, level);
        foreach (var cog in cogs)
        {
            // Only skip using a live, bounded-TTL cached scan so a replaced cloud object with a
            // changed or expanded extent is never permanently masked by stale persisted metadata.
            if (_cache.TryGetValue(MetadataCacheKey(cog.Id), out CachedCogMetadata? cached)
                && cached is not null
                && !ExtentIntersectsTile(cached.Metadata, tileBounds))
            {
                continue;
            }

            var tile = await TryGetTileSafeAsync(new TileRequest(cog, new TileIndex(level, col, row), format), cancellationToken)
                .ConfigureAwait(false);
            if (tile is not null)
            {
                return new CogTileLookup(tile, false);
            }
        }

        return new CogTileLookup(null, false);
    }

    private byte[]? AssembleJpegIfNeeded(PinnedServeRequest serve, CogTileSource source, byte[] tileData)
    {
        var metadata = serve.Pinned.Metadata;
        if (metadata.Compression != "JPEG")
        {
            return tileData;
        }

        var jpeg = JpegTileAssembler.Assemble(tileData, source.Overview.JpegTables, metadata);
        if (jpeg is null)
        {
            var request = serve.Request;
            CogLog.UndecodableJpegTile(_logger, request.Registration.Id, request.Tile.Z, request.Tile.Y, request.Tile.X);
        }

        return jpeg;
    }

    private static (byte[] Data, string ContentType) EncodeRequestedFormat(
        byte[] data,
        string contentType,
        PinnedServeRequest serve)
    {
        var request = serve.Request;
        var metadata = serve.Pinned.Metadata;
        if (contentType != "application/octet-stream")
        {
            return (data, contentType);
        }

        if (request.Format == RasterFormat.PNG)
        {
            var png = CogTileEncoder.EncodePng(data, metadata);
            return png is not null ? (png, "image/png") : (data, contentType);
        }

        if (request.Format is RasterFormat.TIFF or RasterFormat.COG)
        {
            var bounds = TileMath.GetTileBounds(request.Tile.X, request.Tile.Y, request.Tile.Z);
            var tiff = CogTiffTileEncoder.Encode(data, metadata, new RasterExtent
            {
                XMin = bounds.XMin,
                YMin = bounds.YMin,
                XMax = bounds.XMax,
                YMax = bounds.YMax,
                Srid = 3857
            });
            return tiff is not null ? (tiff, "image/tiff") : (data, contentType);
        }

        return (data, contentType);
    }

    private static bool ExtentIntersectsTile(CogMetadata metadata, TileBounds tileBounds)
    {
        if (metadata.Srid != 3857)
        {
            return false;
        }

        var extent = metadata.Extent;
        if (extent.XMax <= extent.XMin || extent.YMax <= extent.YMin)
        {
            return true;
        }

        return extent.XMax > tileBounds.XMin
            && extent.XMin < tileBounds.XMax
            && extent.YMax > tileBounds.YMin
            && extent.YMin < tileBounds.YMax;
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

    // An aligned tile is served as stored. A request finer than the full-resolution level reads the
    // ancestor tile at the COG's native zoom and upsamples the covered quadrant; the codec must
    // decode to raw samples, so JPEG COGs stay native-zoom only.
    private static CogTileSource? ResolveTileSource(CogMetadata metadata, TileIndex tile)
    {
        var overview = FindBestOverviewLevel(metadata, tile.Z);
        if (overview is not null && ResolveStoredTile(metadata, overview, tile) is { } storedIndex)
        {
            return new CogTileSource(overview, storedIndex, null);
        }

        if (metadata.Compression == "JPEG" || metadata.OverviewLevels.Length == 0)
        {
            return null;
        }

        var fullResolution = metadata.OverviewLevels.MaxBy(candidate => candidate.Width)!;
        for (var shift = 1; shift <= tile.Z && CogTileOverzoom.CanUpsample(metadata, shift); shift++)
        {
            var ancestor = new TileIndex(tile.Z - shift, tile.X >> shift, tile.Y >> shift);
            if (ResolveStoredTile(metadata, fullResolution, ancestor) is { } ancestorIndex)
            {
                var quadrantMask = (1 << shift) - 1;
                var quadrant = new CogOverzoomQuadrant(shift, tile.X & quadrantMask, tile.Y & quadrantMask);
                return new CogTileSource(fullResolution, ancestorIndex, quadrant);
            }
        }

        return null;
    }

    private static int? ResolveStoredTile(CogMetadata metadata, CogOverviewLevel overview, TileIndex tile)
        => TryResolveAlignedTileIndex(metadata, overview, tile, out var tileIndex)
            && tileIndex >= 0
            && tileIndex < overview.TileOffsets.Length
            && tileIndex < overview.TileByteCounts.Length
                ? tileIndex
                : null;

    private static bool TryResolveAlignedTileIndex(
        CogMetadata metadata,
        CogOverviewLevel overviewLevel,
        TileIndex tile,
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

        var tileBounds = TileMath.GetTileBounds(tile.X, tile.Y, tile.Z);
        var overviewPixelWidth = extentWidth / overviewLevel.Width;
        var overviewPixelHeight = extentHeight / overviewLevel.Height;

        if (!TryResolvePixelCoordinate((tileBounds.XMin - metadata.Extent.XMin) / overviewPixelWidth, out var minPixelX)
            || !TryResolvePixelCoordinate((tileBounds.XMax - metadata.Extent.XMin) / overviewPixelWidth, out var maxPixelX)
            || !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMax) / overviewPixelHeight, out var minPixelY)
            || !TryResolvePixelCoordinate((metadata.Extent.YMax - tileBounds.YMin) / overviewPixelHeight, out var maxPixelY))
        {
            return false;
        }

        if (maxPixelX - minPixelX != metadata.TileWidth
            || maxPixelY - minPixelY != metadata.TileHeight
            || minPixelX < 0 || minPixelY < 0
            || maxPixelX > overviewLevel.Width || maxPixelY > overviewLevel.Height
            || minPixelX % metadata.TileWidth != 0 || minPixelY % metadata.TileHeight != 0)
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

    private readonly record struct TileRequest(CogRegistration Registration, TileIndex Tile, RasterFormat Format);

    private readonly record struct MetadataLoadRequest(string CacheKey, CogRegistration Registration);

    private readonly record struct PinnedServeRequest(TileRequest Request, CachedCogMetadata Pinned, string MetadataSource);

    private readonly record struct TileReadSpec(TileRequest Request, CachedCogMetadata Pinned, CogTileSource Source);

    private readonly record struct CogTileSource(CogOverviewLevel Overview, int TileIndex, CogOverzoomQuadrant? Quadrant);

    private sealed record CachedCogMetadata(CogMetadata Metadata, string ETag, long ObjectSizeBytes);
}

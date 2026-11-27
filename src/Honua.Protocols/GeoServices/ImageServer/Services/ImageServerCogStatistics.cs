// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// Statistics of a COG-only layer: the 1-based bands to report (null for all) and the histogram size.
// Unsupported names a request parameter the COG path cannot apply; it is answered 501.
internal sealed record ImageServerCogStatisticsRequest(ImageServerCogLayer Layer, int[]? Bands, int BinCount)
{
    public string? Unsupported { get; init; }
}

// Per-band statistics and histograms in band order, or the refusal the request gets instead.
internal sealed record ImageServerCogStatisticsResult(RasterStatistics[] Statistics, RasterHistogram[] Histograms, IResult? Refusal);

// Computes statistics of a layer whose imagery is only registered cloud COGs from each COG's
// coarsest grid-aligned overview, read through the same pinned tile decoder the tile route uses.
// The overview is a full-extent sample of the image, so the statistics are approximate the way
// GDAL's approximate statistics are. Where COGs overlap, the first registration whose extent covers
// a pixel counts it. Work is bounded (tile reads and bands) and results are cached per COG scan,
// because ArcGIS clients ask for service info, which carries these statistics, on every connect.
internal static class ImageServerCogStatistics
{
    internal const int MaxTiles = 16;
    internal const int MaxBands = 16;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    // null unless the layer's imagery is only scanned, readable COGs: the caller keeps its raster_data answer.
    public static async Task<ImageServerCogStatisticsResult?> TryComputeAsync(
        HttpContext context, ImageServerCogStatisticsRequest request, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<ICogTileResolver>();
        var registrations = resolver is null
            ? []
            : (await ImageServerCogRasters.ListCogOnlyAsync(context, request.Layer, cancellationToken).ConfigureAwait(false))
                .Where(IsReadable)
                .ToArray();
        if (registrations.Length == 0)
        {
            return null;
        }

        var bands = SelectBands(request.Bands, registrations);
        var selection = new CogSelection(registrations, bands);
        if (Refuse(context, request, selection) is { } refusal)
        {
            return new ImageServerCogStatisticsResult([], [], refusal);
        }

        var cache = context.RequestServices.GetService<IMemoryCache>();
        var key = CacheKey(context, selection, request.BinCount);
        if (cache?.TryGetValue(key, out ImageServerCogStatisticsResult? cached) == true && cached is not null)
        {
            return cached;
        }

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageServerCogStatistics).FullName!) ?? NullLogger.Instance;
        var source = new ImageServerCogExport.CogTileSource(resolver!, logger) { Format = RasterFormat.Raw };
        var blocks = await ReadBlocksAsync(source, registrations, cancellationToken).ConfigureAwait(false);
        var result = Compute(blocks, bands, request.BinCount);
        if (blocks.Count == registrations.Sum(registration => OverviewTiles(registration).Count))
        {
            cache?.Set(key, result, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CacheLifetime, Size = 1 });
        }
        return result;
    }

    // The tile decoder serves chunky EPSG:3857 COGs with lossless samples on the grid, as identify reads them.
    internal static bool IsReadable(CogRegistration registration)
        => registration.Metadata is { Srid: 3857, Compression: not "JPEG", PlanarConfiguration: 1 } metadata
           && metadata.BitsPerSample is 8 or 16 or 32 or 64
           && ImageServerCogExport.CogCanvas.ServableZooms([registration]).Length > 0;

    // Two passes over the decoded tiles: statistics first, then histograms over each band's range.
    internal static ImageServerCogStatisticsResult Compute(IReadOnlyList<CogPixelBlock> blocks, int[] bands, int binCount)
    {
        var accumulators = bands.Select(band => new BandAccumulator(band)).ToArray();
        foreach (var block in blocks)
        {
            block.Observe(accumulators);
        }

        foreach (var accumulator in accumulators)
        {
            accumulator.StartHistogram(binCount);
        }

        foreach (var block in blocks)
        {
            block.Bin(accumulators);
        }

        return new ImageServerCogStatisticsResult(
            accumulators.Select(accumulator => accumulator.Statistics()).ToArray(),
            accumulators.Select(accumulator => accumulator.Histogram()).ToArray(),
            null);
    }

    internal sealed record CogSelection(CogRegistration[] Registrations, int[] Bands);

    private static IResult? Refuse(HttpContext context, ImageServerCogStatisticsRequest request, CogSelection selection)
    {
        var (registrations, bands) = selection;
        if (!ImageServerCogRasters.IsEntitled(context))
        {
            return ImageServerCogRasters.PaymentRequired(context);
        }

        var reads = registrations.Sum(registration => OverviewTiles(registration).Count);
        var unsupported = request.Unsupported
            ?? (bands.Length > MaxBands ? $"Statistics of cloud COG layers cover at most {MaxBands} bands; select bands with bandIds." : null)
            ?? (reads > MaxTiles ? $"Statistics of these COGs need more than {MaxTiles} tile reads; add coarser overviews to the COGs." : null);
        return unsupported is null ? null : StandardErrorHelpers.CreateNotImplemented(context, unsupported);
    }

    // Requested bands that every COG holds, in request order; all shared bands when none were requested.
    private static int[] SelectBands(int[]? requested, CogRegistration[] registrations)
    {
        var shared = registrations.Min(registration => registration.Metadata!.BandCount);
        return requested is { Length: > 0 }
            ? requested.Where(band => band >= 1 && band <= shared).Distinct().ToArray()
            : Enumerable.Range(1, shared).ToArray();
    }

    internal static string CacheKey(HttpContext context, CogSelection selection, int binCount)
    {
        var tenantId = TenantScopeHelpers.ResolveRequestTenantId(context) ?? "<none>";
        var schema = SchemaContext.AmbientCurrentSchema
            ?? context.RequestServices?.GetService<ISchemaContext>()?.CurrentSchema
            ?? "default";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"imageserver-cog-statistics:t={Uri.EscapeDataString(tenantId)}|s={Uri.EscapeDataString(schema)}|{string.Join(',', selection.Registrations.Select(static registration => $"{registration.Id}@{registration.MetadataScannedAt?.UtcTicks}:{Uri.EscapeDataString(registration.Bucket ?? string.Empty)}:{Uri.EscapeDataString(registration.ObjectKey ?? string.Empty)}"))}|{string.Join(',', selection.Bands)}|{binCount}");
    }

    private static List<ImageServerCogExport.TileAddress> OverviewTiles(CogRegistration registration)
    {
        var zoom = ImageServerCogExport.CogCanvas.ServableZooms([registration])[0];
        return ImageServerCogExport.CogCanvas.Cover(registration.Metadata!.Extent, zoom);
    }

    private static async Task<List<CogPixelBlock>> ReadBlocksAsync(
        ImageServerCogExport.CogTileSource source, CogRegistration[] registrations, CancellationToken cancellationToken)
    {
        var reads = registrations
            .SelectMany((registration, order) => OverviewTiles(registration)
                .Select(tile => new ImageServerCogExport.TileRead(order, registration, tile)))
            .ToArray();
        var tiles = await source.ReadAllAsync(reads, cancellationToken).ConfigureAwait(false);
        return reads
            .Where(tiles.ContainsKey)
            .Select(read => new CogPixelBlock(read, tiles[read], registrations[..read.Order]))
            .ToList();
    }

    // One decoded tile of a COG, and the earlier registrations whose extent takes precedence.
    internal sealed record CogPixelBlock(ImageServerCogExport.TileRead Read, byte[] Samples, CogRegistration[] Earlier)
    {
        public void Observe(BandAccumulator[] accumulators)
            => FeedPixels(accumulators, FeedMode.Observe);

        public void Bin(BandAccumulator[] accumulators)
            => FeedPixels(accumulators, FeedMode.Bin);

        // Feeds every valid sample of each pixel whose centre lies inside this COG and no earlier one.
        private void FeedPixels(BandAccumulator[] accumulators, FeedMode mode)
        {
            var metadata = Read.Registration.Metadata!;
            var bounds = TileMath.GetTileBounds(Read.Tile.X, Read.Tile.Y, Read.Tile.Z);
            var pixelRange = ComputeClippedPixelRange(metadata, bounds);
            if (pixelRange.IsEmpty)
            {
                return;
            }

            var overlappingEarlier = CollectOverlappingExtents(Earlier, bounds);
            var noData = ImageServerCogIdentify.ParseFiniteNoData(metadata);
            Span<double> bandBuffer = metadata.BandCount <= MaxBands
                ? stackalloc double[metadata.BandCount]
                : new double[metadata.BandCount];

            var context = new TileFeedContext
            {
                Metadata = metadata,
                Bounds = bounds,
                Range = pixelRange,
                OverlappingEarlier = overlappingEarlier,
                NoData = noData,
                Mode = mode,
            };
            FeedTilePixels(accumulators, bandBuffer, context);
        }

        private void FeedTilePixels(BandAccumulator[] accumulators, Span<double> bandBuffer, TileFeedContext context)
        {
            var stepX = (context.Bounds.XMax - context.Bounds.XMin) / context.Metadata.TileWidth;
            var stepY = (context.Bounds.YMax - context.Bounds.YMin) / context.Metadata.TileHeight;

            for (var row = context.Range.MinRow; row < context.Range.MaxRow; row++)
            {
                var y = context.Bounds.YMax - ((row + 0.5) * stepY);
                var rowContext = new RowFeedContext
                {
                    Tile = context,
                    RowOffset = row * context.Metadata.TileWidth,
                    Y = y,
                    StepX = stepX,
                };
                FeedRowPixels(accumulators, bandBuffer, rowContext);
            }
        }

        private void FeedRowPixels(BandAccumulator[] accumulators, Span<double> bandBuffer, RowFeedContext row)
        {
            var context = row.Tile;
            var hasEarlier = context.OverlappingEarlier.Length > 0;
            for (var col = context.Range.MinCol; col < context.Range.MaxCol; col++)
            {
                if (hasEarlier && IsCoveredByEarlier(context.OverlappingEarlier, context.Bounds.XMin + ((col + 0.5) * row.StepX), row.Y))
                {
                    continue;
                }

                if (!ImageServerCogIdentify.TryReadBands(Samples, new ImageServerCogIdentify.BandReadSpec(context.Metadata, row.RowOffset + col), bandBuffer))
                {
                    continue;
                }

                AccumulateBands(accumulators, bandBuffer, context);
            }
        }

        private static void AccumulateBands(BandAccumulator[] accumulators, ReadOnlySpan<double> bands, TileFeedContext context)
        {
            var hasNoData = context.NoData.HasValue;
            var noDataValue = context.NoData.GetValueOrDefault();
            for (var i = 0; i < accumulators.Length; i++)
            {
                var accumulator = accumulators[i];
                var value = accumulator.Band <= bands.Length ? bands[accumulator.Band - 1] : double.NaN;
                if (!double.IsFinite(value) || (hasNoData && ImageServerCogIdentify.IsExactMatch(value, noDataValue)))
                {
                    continue;
                }

                if (context.Mode == FeedMode.Bin)
                {
                    accumulator.Bin(value);
                }
                else
                {
                    accumulator.Observe(value);
                }
            }
        }

        private static PixelRange ComputeClippedPixelRange(CogMetadata metadata, TileBounds bounds)
        {
            var spanX = bounds.XMax - bounds.XMin;
            var spanY = bounds.YMax - bounds.YMin;
            if (spanX <= 0 || spanY <= 0 || metadata.TileWidth <= 0 || metadata.TileHeight <= 0)
            {
                return default;
            }

            var extent = metadata.Extent;
            return new PixelRange
            {
                MinCol = Math.Clamp((int)Math.Ceiling(((extent.XMin - bounds.XMin) / spanX * metadata.TileWidth) - 0.5), 0, metadata.TileWidth),
                MaxCol = Math.Clamp((int)Math.Ceiling(((extent.XMax - bounds.XMin) / spanX * metadata.TileWidth) - 0.5), 0, metadata.TileWidth),
                MinRow = Math.Clamp((int)Math.Ceiling(((bounds.YMax - extent.YMax) / spanY * metadata.TileHeight) - 0.5), 0, metadata.TileHeight),
                MaxRow = Math.Clamp((int)Math.Ceiling(((bounds.YMax - extent.YMin) / spanY * metadata.TileHeight) - 0.5), 0, metadata.TileHeight),
            };
        }

        private static RasterExtent[] CollectOverlappingExtents(CogRegistration[] earlier, TileBounds bounds)
        {
            if (earlier.Length == 0)
            {
                return [];
            }

            var overlapping = new List<RasterExtent>(earlier.Length);
            for (var i = 0; i < earlier.Length; i++)
            {
                var extent = earlier[i].Metadata!.Extent;
                if (extent.XMax > bounds.XMin && extent.XMin < bounds.XMax
                    && extent.YMax > bounds.YMin && extent.YMin < bounds.YMax)
                {
                    overlapping.Add(extent);
                }
            }

            return overlapping.Count == 0 ? [] : overlapping.ToArray();
        }

        private static bool IsCoveredByEarlier(RasterExtent[] earlierExtents, double x, double y)
        {
            for (var i = 0; i < earlierExtents.Length; i++)
            {
                var extent = earlierExtents[i];
                if (x >= extent.XMin && x < extent.XMax && y > extent.YMin && y <= extent.YMax)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private enum FeedMode
    {
        Observe,
        Bin,
    }

    private readonly record struct PixelRange
    {
        public int MinCol { get; init; }

        public int MaxCol { get; init; }

        public int MinRow { get; init; }

        public int MaxRow { get; init; }

        public bool IsEmpty => MinCol >= MaxCol || MinRow >= MaxRow;
    }

    private readonly record struct TileFeedContext
    {
        public required CogMetadata Metadata { get; init; }

        public required TileBounds Bounds { get; init; }

        public required PixelRange Range { get; init; }

        public required RasterExtent[] OverlappingEarlier { get; init; }

        public double? NoData { get; init; }

        public required FeedMode Mode { get; init; }
    }

    private readonly record struct RowFeedContext
    {
        public required TileFeedContext Tile { get; init; }

        public required int RowOffset { get; init; }

        public required double Y { get; init; }

        public required double StepX { get; init; }
    }

    // Streaming population statistics (Welford), then a histogram over the band's own [min, max].
    internal sealed class BandAccumulator(int band)
    {
        private long _count;
        private double _mean;
        private double _squares;
        private double _min = double.PositiveInfinity;
        private double _max = double.NegativeInfinity;
        private long[] _counts = [];

        public int Band => band;

        public void Observe(double value)
        {
            _count++;
            var delta = value - _mean;
            _mean += delta / _count;
            _squares += delta * (value - _mean);
            _min = Math.Min(_min, value);
            _max = Math.Max(_max, value);
        }

        public void StartHistogram(int binCount) => _counts = new long[binCount];

        public void Bin(double value)
        {
            var width = _max - _min;
            var bin = width > 0 ? (int)((value - _min) / width * _counts.Length) : 0;
            _counts[Math.Clamp(bin, 0, _counts.Length - 1)]++;
        }

        public RasterStatistics Statistics() => _count == 0
            ? new RasterStatistics { Band = band }
            : new RasterStatistics
            {
                Band = band,
                MinValue = _min,
                MaxValue = _max,
                MeanValue = _mean,
                StandardDeviation = Math.Sqrt(_squares / _count),
                ValidPixelCount = _count,
            };

        public RasterHistogram Histogram() => new()
        {
            Band = band,
            BinCount = _counts.Length,
            Min = _count == 0 ? 0 : _min,
            Max = _count == 0 ? 0 : _max,
            Counts = _counts,
        };
    }
}

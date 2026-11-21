// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
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
        if (Refuse(context, request, new CogSelection(registrations, bands)) is { } refusal)
        {
            return new ImageServerCogStatisticsResult([], [], refusal);
        }

        var cache = context.RequestServices.GetService<IMemoryCache>();
        var key = CacheKey(registrations, bands, request.BinCount);
        if (cache?.TryGetValue(key, out ImageServerCogStatisticsResult? cached) == true && cached is not null)
        {
            return cached;
        }

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageServerCogStatistics).FullName!)
            ?? NullLogger.Instance;
        var source = new ImageServerCogExport.CogTileSource(resolver!, logger) { Format = RasterFormat.Raw };
        var blocks = await ReadBlocksAsync(source, registrations, cancellationToken).ConfigureAwait(false);
        var result = Compute(blocks, bands, request.BinCount);

        // A failed tile read leaves the statistics partial; answer them, but do not keep them.
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
            block.Feed(accumulators, static (accumulator, value) => accumulator.Observe(value));
        }

        foreach (var accumulator in accumulators)
        {
            accumulator.StartHistogram(binCount);
        }

        foreach (var block in blocks)
        {
            block.Feed(accumulators, static (accumulator, value) => accumulator.Bin(value));
        }

        return new ImageServerCogStatisticsResult(
            accumulators.Select(accumulator => accumulator.Statistics()).ToArray(),
            accumulators.Select(accumulator => accumulator.Histogram()).ToArray(),
            null);
    }

    private sealed record CogSelection(CogRegistration[] Registrations, int[] Bands);

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

    private static string CacheKey(CogRegistration[] registrations, int[] bands, int binCount)
        => string.Create(CultureInfo.InvariantCulture, $"imageserver-cog-statistics:{string.Join(',', registrations.Select(
            registration => $"{registration.Id}@{registration.MetadataScannedAt?.UtcTicks}"))}|{string.Join(',', bands)}|{binCount}");

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
        // Feeds every valid sample of each pixel whose centre lies inside this COG and no earlier one.
        public void Feed(IReadOnlyList<BandAccumulator> accumulators, Action<BandAccumulator, double> observe)
        {
            var metadata = Read.Registration.Metadata!;
            var sink = new PixelSink(accumulators, observe, NoDataSample(metadata));
            var bounds = TileMath.GetTileBounds(Read.Tile.X, Read.Tile.Y, Read.Tile.Z);
            var pixels = metadata.TileWidth * metadata.TileHeight;
            for (var index = 0; index < pixels; index++)
            {
                if (OwnedBands(metadata, bounds, index) is { } bands)
                {
                    sink.Feed(bands);
                }
            }
        }

        private double[]? OwnedBands(CogMetadata metadata, TileBounds bounds, int index)
        {
            var row = index / metadata.TileWidth;
            var column = index % metadata.TileWidth;
            var x = bounds.XMin + ((column + 0.5) * (bounds.XMax - bounds.XMin) / metadata.TileWidth);
            var y = bounds.YMax - ((row + 0.5) * (bounds.YMax - bounds.YMin) / metadata.TileHeight);
            return Contains(metadata.Extent, x, y) && !Earlier.Any(earlier => Contains(earlier.Metadata!.Extent, x, y))
                ? ImageServerCogIdentify.ReadBands(Samples, metadata, index)
                : null;
        }

        // A float32 NoData is compared at float32 precision, as the decoded samples carry it.
        private static double? NoDataSample(CogMetadata metadata)
        {
            if (!double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            return metadata.PixelType == "float32" ? (float)value : value;
        }

        private static bool Contains(RasterExtent extent, double x, double y)
            => x >= extent.XMin && x < extent.XMax && y > extent.YMin && y <= extent.YMax;
    }

    // Hands a pixel's valid band samples (finite and not NoData) to the band accumulators.
    private readonly record struct PixelSink(IReadOnlyList<BandAccumulator> Accumulators, Action<BandAccumulator, double> Observe, double? NoData)
    {
        public void Feed(double[] bands)
        {
            foreach (var accumulator in Accumulators)
            {
                var value = accumulator.Band <= bands.Length ? bands[accumulator.Band - 1] : double.NaN;
                if (double.IsFinite(value) && !value.Equals(NoData))
                {
                    Observe(accumulator, value);
                }
            }
        }
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

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// An exportImage on a layer whose imagery is only registered cloud COGs: the publication the COGs
// are bound to, the request (bbox and bboxSR in Web Mercator) and its parsed export query.
internal sealed record ImageServerCogExportRequest(ImageServerCogLayer Layer, ExportImageRequest Request, RasterQuery Query);

// The composed image, or the refusal the request gets instead.
internal sealed record ImageServerCogExportResult(RasterResult? Image, IResult? Refusal);

// Composes exportImage from the same pinned, decoded tiles the tile route serves, so a dynamic
// ImageryLayer draws a COG-only layer. The tile route serves EPSG:3857 COGs aligned to the
// GoogleMapsCompatible grid; each COG is read at its overview whose zoom is closest to (and not
// coarser than) the requested resolution, over the part of the canvas it covers, and resampled.
internal static class ImageServerCogExport
{
    internal const int MaxTiles = 64;
    internal const double WorldSpan = 2 * Math.PI * 6378137d;
    private const int MaxParallelReads = 8;
    private const int DefaultOutputDimension = 400;
    private const double EdgeTolerance = 1e-6;

    // null unless the layer's imagery is only scanned COGs: the caller keeps its raster_data answer.
    public static async Task<ImageServerCogExportResult?> TryRenderAsync(
        HttpContext context, ImageServerCogExportRequest request, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<ICogTileResolver>();
        var registrations = resolver is null
            ? []
            : (await ImageServerCogRasters.ListCogOnlyAsync(context, request.Layer, cancellationToken).ConfigureAwait(false))
                .Where(registration => registration.Metadata!.Srid == 3857)
                .ToArray();
        if (registrations.Length == 0)
        {
            return null;
        }

        var refusal = Refuse(context, request);
        if (refusal is not null)
        {
            return new ImageServerCogExportResult(null, refusal);
        }

        var canvas = CogCanvas.Create(request, registrations);
        if (canvas.Reads.Count > MaxTiles)
        {
            return new ImageServerCogExportResult(null, StandardErrorHelpers.CreateBadRequest(context,
                $"The export needs more than {MaxTiles} COG tile reads; request a smaller extent or size."));
        }

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageServerCogExport).FullName!)
            ?? NullLogger.Instance;
        var tiles = await new CogTileSource(resolver!, logger).ReadAllAsync(canvas.Reads, cancellationToken).ConfigureAwait(false);
        return new ImageServerCogExportResult(Compose(canvas, tiles, request.Query), null);
    }

    private static IResult? Refuse(HttpContext context, ImageServerCogExportRequest request)
    {
        if (!ImageServerCogRasters.IsEntitled(context))
        {
            return ImageServerCogRasters.PaymentRequired(context);
        }

        var unsupported = Unsupported(request);
        return unsupported is null ? null : StandardErrorHelpers.CreateNotImplemented(context, unsupported);
    }

    // The tile route hands out raw/jpeg tiles in Web Mercator, so that is what composes.
    internal static string? Unsupported(ImageServerCogExportRequest request)
    {
        var query = request.Query;
        if (query.OutputFormat is not (RasterFormat.PNG or RasterFormat.JPEG))
        {
            return "Cloud COG layers export png or jpg; pixel blocks and TIFF read raster_data only.";
        }

        if (query.Bands is { Length: > 0 } || query.Stretch is not null || query.Colormap is not null
            || query.BandArithmetic is not null || query.Terrain is not null || query.RenderingClip is not null)
        {
            return "renderingRule and bandIds are not applied to cloud COG layers.";
        }

        var bboxSrid = SpatialReferenceHelpers.TryParseSrid(request.Request.BboxSr);
        return IsWebMercator(bboxSrid) && IsWebMercator(query.OutputSrid)
            ? null
            : "Cloud COG layers export in Web Mercator; use bboxSR and imageSR 3857 (102100 is the same system).";
    }

    internal static bool IsWebMercator(int? srid) => srid is null || SpatialReferenceExtensions.IsWebMercatorSrid(srid.Value);

    internal static bool CanDecodeRawSamples(CogMetadata metadata)
        => metadata.Compression != "JPEG"
           && metadata.PlanarConfiguration == 1
           && metadata.BitsPerSample is 8 or 16
           && metadata.PixelType == $"uint{metadata.BitsPerSample}"
           && ((metadata.BandCount == 1 && metadata.PhotometricInterpretation is 0 or 1)
               || (metadata.BandCount is 3 or 4 && metadata.PhotometricInterpretation is 1 or 2));

    // Later registrations are drawn first so the first registration holding a pixel wins, as in
    // the tile route's layer lookup; transparent (NoData) pixels let the next COG show through.
    private static RasterResult Compose(CogCanvas canvas, ConcurrentDictionary<TileRead, byte[]> tiles, RasterQuery query)
    {
        var imageInfo = new SKImageInfo(canvas.Width, canvas.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo);
        EnsureAllocated(surface, $"Failed to allocate Skia surface ({canvas.Width}x{canvas.Height}).");
        surface.Canvas.Clear(SKColors.Transparent);
        var sampling = query.ResamplingAlgorithm == ResamplingAlgorithm.NearestNeighbor
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);

        foreach (var read in canvas.Reads.Reverse())
        {
            using var image = tiles.TryGetValue(read, out var data)
                ? DecodeTileImage(data, read.Registration.Metadata!)
                : null;
            if (image is not null)
            {
                surface.Canvas.DrawImage(image, canvas.Destination(read.Tile), sampling);
            }
        }

        var jpeg = query.OutputFormat == RasterFormat.JPEG;
        using var snapshot = surface.Snapshot();
        EnsureAllocated(snapshot, "Failed to snapshot composed COG export surface.");
        using var encoded = snapshot.Encode(jpeg ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, query.Quality ?? 80);
        EnsureAllocated(encoded, "Failed to encode composed COG export image.");
        return new RasterResult
        {
            Data = encoded.ToArray(),
            ContentType = jpeg ? "image/jpeg" : "image/png",
            Width = canvas.Width,
            Height = canvas.Height,
            BandCount = 4,
            Srid = 3857,
            Extent = canvas.Extent,
        };
    }

    private static void EnsureAllocated(object? value, string message)
    {
        if (value is null)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static SKImage? DecodeTileImage(byte[] data, CogMetadata metadata)
        => CanDecodeRawSamples(metadata)
            ? CreateImageFromRawSamples(data, metadata)
            : SKImage.FromEncodedData(data);

    // Converts decoded chunky uint8/uint16 samples straight to RGBA8888 without PNG encode+decode.
    private static SKImage? CreateImageFromRawSamples(byte[] samples, CogMetadata metadata)
    {
        var bytesPerSample = metadata.BitsPerSample / 8;
        var pixelCount = checked(metadata.TileWidth * metadata.TileHeight);
        var expectedBytes = checked((long)pixelCount * metadata.BandCount * bytesPerSample);
        if (metadata.TileWidth <= 0 || metadata.TileHeight <= 0 || samples.Length != expectedBytes)
        {
            return null;
        }

        if (!TryParseIntegerNoData(metadata, out var hasNoData, out var noData))
        {
            return null;
        }

        var rgba = new byte[checked(pixelCount * 4)];
        var invertGray = metadata.BandCount == 1 && metadata.PhotometricInterpretation == 0;
        var maxSample = metadata.BitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue;
        FillRgbaFromRaw(samples, rgba, new RawPixelContext(metadata, new RawNoDataSpec(hasNoData, noData), invertGray, maxSample));
        var info = new SKImageInfo(metadata.TileWidth, metadata.TileHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return SKImage.FromPixelCopy(info, rgba);
    }

    private static bool TryParseIntegerNoData(CogMetadata metadata, out bool hasNoData, out int noData)
    {
        hasNoData = false;
        noData = 0;
        if (metadata.NoData is null)
        {
            return true;
        }

        var maximum = metadata.BitsPerSample == 8 ? byte.MaxValue : ushort.MaxValue;
        if (!double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < 0 || value > maximum
            || BitConverter.DoubleToInt64Bits(value + 0d) != BitConverter.DoubleToInt64Bits(Math.Truncate(value) + 0d))
        {
            return false;
        }

        hasNoData = true;
        noData = (int)value;
        return true;
    }

    private static void FillRgbaFromRaw(ReadOnlySpan<byte> samples, Span<byte> rgba, RawPixelContext context)
    {
        var metadata = context.Metadata;
        var stride = metadata.BandCount * (metadata.BitsPerSample / 8);
        var pixelCount = rgba.Length / 4;

        for (var i = 0; i < pixelCount; i++)
        {
            WriteRgbaPixel(samples.Slice(i * stride, stride), rgba.Slice(i * 4, 4), context);
        }
    }

    private static void WriteRgbaPixel(ReadOnlySpan<byte> src, Span<byte> dst, RawPixelContext context)
    {
        var metadata = context.Metadata;
        if (metadata.BandCount == 1)
        {
            var raw = ReadUnsignedSample(src, 0, metadata);
            var isNoData = context.NoData.HasNoData && raw == context.NoData.Value;
            var display = context.InvertGray ? context.MaxSample - raw : raw;
            var gray = ToByte(display, metadata.BitsPerSample);
            dst[0] = gray;
            dst[1] = gray;
            dst[2] = gray;
            dst[3] = isNoData ? (byte)0 : (byte)255;
            return;
        }

        var bytesPerSample = metadata.BitsPerSample / 8;
        var r = ReadUnsignedSample(src, 0, metadata);
        var g = ReadUnsignedSample(src, bytesPerSample, metadata);
        var b = ReadUnsignedSample(src, bytesPerSample * 2, metadata);
        var a = metadata.BandCount == 4
            ? ToByte(ReadUnsignedSample(src, bytesPerSample * 3, metadata), metadata.BitsPerSample)
            : (context.NoData.HasNoData && r == context.NoData.Value && g == context.NoData.Value && b == context.NoData.Value ? (byte)0 : (byte)255);
        dst[0] = ToByte(r, metadata.BitsPerSample);
        dst[1] = ToByte(g, metadata.BitsPerSample);
        dst[2] = ToByte(b, metadata.BitsPerSample);
        dst[3] = a;
    }

    private static int ReadUnsignedSample(ReadOnlySpan<byte> src, int offset, CogMetadata metadata)
        => metadata.BitsPerSample == 8
            ? src[offset]
            : metadata.IsLittleEndian
                ? BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(offset))
                : BinaryPrimitives.ReadUInt16BigEndian(src.Slice(offset));

    private static byte ToByte(int value, int bitsPerSample)
        => bitsPerSample == 16 ? (byte)(value >> 8) : (byte)value;

    private readonly record struct RawNoDataSpec(bool HasNoData, int Value);

    private readonly record struct RawPixelContext(CogMetadata Metadata, RawNoDataSpec NoData, bool InvertGray, int MaxSample);

    internal readonly record struct TileAddress(int Z, int X, int Y);

    // One grid tile of one COG; Order is the registration's position in store order.
    internal sealed record TileRead(int Order, CogRegistration Registration, TileAddress Tile);

    // Reads tiles through the tile route's resolver. Each COG's first tile is read alone so its
    // metadata scan is cached before the rest are read in parallel. Format overrides the rendered
    // tile format (statistics read raw samples).
    internal sealed record CogTileSource(ICogTileResolver Resolver, ILogger Logger)
    {
        public RasterFormat? Format { get; init; }

        public async Task<ConcurrentDictionary<TileRead, byte[]>> ReadAllAsync(IReadOnlyList<TileRead> reads, CancellationToken cancellationToken)
        {
            var tiles = new ConcurrentDictionary<TileRead, byte[]>();
            var firsts = reads.GroupBy(read => read.Order).Select(group => group.First()).ToArray();
            foreach (var first in firsts)
            {
                await ReadIntoAsync(tiles, first, cancellationToken).ConfigureAwait(false);
            }

            await Parallel.ForEachAsync(
                reads.Except(firsts),
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = cancellationToken },
                (read, token) => new ValueTask(ReadIntoAsync(tiles, read, token))).ConfigureAwait(false);
            return tiles;
        }

        private async Task ReadIntoAsync(ConcurrentDictionary<TileRead, byte[]> tiles, TileRead read, CancellationToken cancellationToken)
        {
            var data = await TryReadAsync(read, cancellationToken).ConfigureAwait(false);
            if (data is not null)
            {
                tiles[read] = data;
            }
        }

        // JPEG COGs leave the tile route as image/jpeg only; raw-decodable COGs request Raw to avoid
        // redundant PNG encode+decode cycles; any other codec falls back to PNG.
        private async Task<byte[]?> TryReadAsync(TileRead read, CancellationToken cancellationToken)
        {
            var metadata = read.Registration.Metadata!;
            var format = Format ?? (metadata.Compression == "JPEG"
                ? RasterFormat.JPEG
                : CanDecodeRawSamples(metadata) ? RasterFormat.Raw : RasterFormat.PNG);
            try
            {
                var result = await Resolver.GetTileAsync(read.Registration, read.Tile.Z, read.Tile.Y, read.Tile.X, format, cancellationToken)
                    .ConfigureAwait(false);
                return result is { Data.Length: > 0 } found ? found.Data : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                ImageServerLog.CogTileReadSkipped(Logger, ex, read.Registration.Id, read.Tile.Z, read.Tile.Y, read.Tile.X);
                return null;
            }
        }
    }

    // The output canvas in Web Mercator and the COG tiles that cover it.
    internal sealed record CogCanvas(RasterExtent Extent, int Width, int Height)
    {
        public required IReadOnlyList<TileRead> Reads { get; init; }

        public static CogCanvas Create(ImageServerCogExportRequest request, CogRegistration[] registrations)
        {
            var extent = RequestedExtent(request.Request.Bbox, registrations);
            var width = request.Query.OutputWidth ?? DefaultOutputDimension;
            var height = request.Query.OutputHeight ?? DefaultOutputDimension;
            var resolution = Math.Max((extent.XMax - extent.XMin) / width, (extent.YMax - extent.YMin) / height);
            var reads = registrations
                .SelectMany((registration, order) => CoverRegistration(registration, extent, resolution)
                    .Select(tile => new TileRead(order, registration, tile)))
                .ToArray();
            return new CogCanvas(extent, width, height) { Reads = reads };
        }

        public SKRect Destination(TileAddress tile)
        {
            var bounds = TileMath.GetTileBounds(tile.X, tile.Y, tile.Z);
            var scaleX = Width / (Extent.XMax - Extent.XMin);
            var scaleY = Height / (Extent.YMax - Extent.YMin);
            return new SKRect(
                (float)((bounds.XMin - Extent.XMin) * scaleX),
                (float)((Extent.YMax - bounds.YMax) * scaleY),
                (float)((bounds.XMax - Extent.XMin) * scaleX),
                (float)((Extent.YMax - bounds.YMin) * scaleY));
        }

        private static List<TileAddress> CoverRegistration(CogRegistration registration, RasterExtent canvas, double resolution)
        {
            var zooms = ServableZooms([registration]);
            if (Intersect(canvas, registration.Metadata!.Extent) is not { } covered || zooms.Length == 0)
            {
                return [];
            }

            var zoom = ChooseZoom(zooms, NeededZoom(resolution, registration.Metadata.TileWidth));
            var tiles = Cover(covered, zoom);
            foreach (var coarser in zooms.Where(candidate => candidate < zoom).OrderDescending())
            {
                if (tiles.Count <= MaxTiles)
                {
                    break;
                }

                tiles = Cover(covered, coarser);
            }

            return tiles;
        }

        private static RasterExtent RequestedExtent(string? bbox, CogRegistration[] registrations)
        {
            if (!string.IsNullOrEmpty(bbox)
                && RasterParsingHelpers.TryParseBoundingBox(bbox, out var minX, out var minY, out var maxX, out var maxY))
            {
                return new RasterExtent { XMin = minX, YMin = minY, XMax = maxX, YMax = maxY, Srid = 3857 };
            }

            var extents = registrations.Select(registration => registration.Metadata!.Extent).ToArray();
            return new RasterExtent
            {
                XMin = extents.Min(extent => extent.XMin),
                YMin = extents.Min(extent => extent.YMin),
                XMax = extents.Max(extent => extent.XMax),
                YMax = extents.Max(extent => extent.YMax),
                Srid = 3857,
            };
        }

        private static RasterExtent? Intersect(RasterExtent a, RasterExtent b)
        {
            var intersection = new RasterExtent
            {
                XMin = Math.Max(a.XMin, b.XMin),
                YMin = Math.Max(a.YMin, b.YMin),
                XMax = Math.Min(a.XMax, b.XMax),
                YMax = Math.Min(a.YMax, b.YMax),
                Srid = 3857,
            };
            return intersection.XMax > intersection.XMin && intersection.YMax > intersection.YMin ? intersection : null;
        }

        // Each overview whose tiles are exactly one grid tile is served at that zoom.
        internal static int[] ServableZooms(CogRegistration[] registrations)
            => registrations
                .SelectMany(registration => OverviewWidths(registration.Metadata!)
                    .Select(width => GridZoom(registration.Metadata!, width)))
                .OfType<int>()
                .Distinct()
                .Order()
                .ToArray();

        private static IEnumerable<int> OverviewWidths(CogMetadata metadata)
            => metadata.OverviewLevels.Length > 0
                ? metadata.OverviewLevels.Select(overview => overview.Width)
                : [metadata.Width];

        private static int? GridZoom(CogMetadata metadata, int overviewWidth)
        {
            var tileSpan = (metadata.Extent.XMax - metadata.Extent.XMin) / overviewWidth * metadata.TileWidth;
            var zoom = Math.Log2(WorldSpan / tileSpan);
            var rounded = (int)Math.Round(zoom);
            return overviewWidth > 0 && Math.Abs(zoom - rounded) < EdgeTolerance && rounded is >= 0 and <= TileMath.MaxSupportedZoomLevel
                ? rounded
                : null;
        }

        private static int NeededZoom(double resolution, int tileWidth)
            => (int)Math.Clamp(Math.Ceiling(Math.Log2(WorldSpan / (tileWidth * resolution)) - 1e-9), 0, TileMath.MaxSupportedZoomLevel);

        internal static int ChooseZoom(int[] zooms, int needed)
            => zooms.Where(zoom => zoom >= needed).DefaultIfEmpty(zooms[^1]).Min();

        internal static List<TileAddress> Cover(RasterExtent extent, int zoom)
        {
            var count = 1 << zoom;
            var span = WorldSpan / count;
            var origin = WorldSpan / 2;
            var minX = FirstTile((extent.XMin + origin) / span, count);
            var maxX = LastTile((extent.XMax + origin) / span, count);
            var minY = FirstTile((origin - extent.YMax) / span, count);
            var maxY = LastTile((origin - extent.YMin) / span, count);
            var tiles = new List<TileAddress>();
            for (var y = minY; y <= maxY && tiles.Count <= MaxTiles; y++)
            {
                tiles.AddRange(Enumerable.Range(minX, maxX - minX + 1).Select(x => new TileAddress(zoom, x, y)));
            }

            return tiles;
        }

        private static int FirstTile(double position, int count)
            => Math.Clamp((int)Math.Floor(position + EdgeTolerance), 0, count - 1);

        private static int LastTile(double position, int count)
            => Math.Clamp((int)Math.Ceiling(position - EdgeTolerance) - 1, 0, count - 1);
    }
}

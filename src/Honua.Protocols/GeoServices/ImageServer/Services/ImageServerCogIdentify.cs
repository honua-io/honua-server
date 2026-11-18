// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// An identify point on a layer whose imagery is only registered cloud COGs.
internal sealed record ImageServerCogIdentifyRequest(ImageServerCogLayer Layer, double X, double Y)
{
    public int? Srid { get; init; }

    // A renderingRule asks for the rendered value; COG pixels are raw samples only.
    public bool HasRenderingRule { get; init; }
}

// The COG that answered (described as a raster) with its pixel, or the refusal instead.
internal sealed record ImageServerCogIdentifyResult(RasterInfo? Raster, PixelValueResult? Pixel, IResult? Refusal);

// Reads the pixel under a point from the finest grid-aligned tile of the first registered COG
// covering it, through the tile route's pinned resolver as raw samples. JPEG COGs are lossy and
// leave the resolver only as JPEG, so they have no sample to report.
internal static class ImageServerCogIdentify
{
    // null when the layer is not COG-only or no readable COG covers the point: the caller answers
    // its NoData document. The point's reference is checked first, because a point in another
    // reference never lands inside a Web Mercator extent.
    public static async Task<ImageServerCogIdentifyResult?> TryReadAsync(
        HttpContext context, ImageServerCogIdentifyRequest request, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<ICogTileResolver>();
        var registrations = resolver is null
            ? []
            : await ImageServerCogRasters.ListCogOnlyAsync(context, request.Layer, cancellationToken).ConfigureAwait(false);
        if (registrations.Length == 0)
        {
            return null;
        }

        if (Unsupported(request) is { } unsupported)
        {
            return new ImageServerCogIdentifyResult(null, null, StandardErrorHelpers.CreateNotImplemented(context, unsupported));
        }

        registrations = registrations.Where(registration => Covers(registration.Metadata!, request)).ToArray();
        if (registrations.Length == 0)
        {
            return null;
        }

        if (!ImageServerCogRasters.IsEntitled(context))
        {
            return new ImageServerCogIdentifyResult(null, null, ImageServerCogRasters.PaymentRequired(context));
        }

        var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageServerCogIdentify).FullName!)
            ?? NullLogger.Instance;
        var reader = new CogPixelReader(resolver!, request) { Logger = logger };
        return await ReadFirstWithDataAsync(reader, registrations, cancellationToken).ConfigureAwait(false);
    }

    // An absent sr is the service's own reference, Web Mercator for these layers.
    private static string? Unsupported(ImageServerCogIdentifyRequest request)
        => !ImageServerCogExport.IsWebMercator(request.Srid)
            ? "Identify on cloud COG layers takes a Web Mercator point; use sr 3857 or 102100."
            : request.HasRenderingRule
                ? "renderingRule is not applied to cloud COG layers; identify reports their raw samples."
                : null;

    // The first COG holding data at the point wins, as export draws it; when every covering COG
    // holds NoData there, the first one reports it.
    private static async Task<ImageServerCogIdentifyResult?> ReadFirstWithDataAsync(
        CogPixelReader reader, CogRegistration[] registrations, CancellationToken cancellationToken)
    {
        ImageServerCogIdentifyResult? noData = null;
        foreach (var registration in registrations)
        {
            var pixel = await reader.TryReadAsync(registration, cancellationToken).ConfigureAwait(false);
            var result = pixel is null ? null : new ImageServerCogIdentifyResult(ImageServerCogRasters.Describe(registration), pixel, null);
            if (pixel is { HasData: true })
            {
                return result;
            }

            noData ??= result;
        }

        return noData;
    }

    private static bool Covers(CogMetadata metadata, ImageServerCogIdentifyRequest request)
        => metadata.Srid == 3857 && metadata.Compression != "JPEG" && metadata.PlanarConfiguration == 1
            && request.X >= metadata.Extent.XMin && request.X < metadata.Extent.XMax
            && request.Y > metadata.Extent.YMin && request.Y <= metadata.Extent.YMax;

    private sealed record CogPixelReader(ICogTileResolver Resolver, ImageServerCogIdentifyRequest Request)
    {
        public required ILogger Logger { get; init; }

        public async Task<PixelValueResult?> TryReadAsync(CogRegistration registration, CancellationToken cancellationToken)
        {
            var metadata = registration.Metadata!;
            if (ImageServerCogExport.CogCanvas.ServableZooms([registration]) is not [.., var zoom])
            {
                return null;
            }

            var tile = TileAt(Request.X, Request.Y, zoom);
            var raw = await TryReadRawAsync(registration, tile, cancellationToken).ConfigureAwait(false);
            var bounds = TileMath.GetTileBounds(tile.X, tile.Y, zoom);
            var column = Math.Clamp((int)((Request.X - bounds.XMin) / (bounds.XMax - bounds.XMin) * metadata.TileWidth), 0, metadata.TileWidth - 1);
            var row = Math.Clamp((int)((bounds.YMax - Request.Y) / (bounds.YMax - bounds.YMin) * metadata.TileHeight), 0, metadata.TileHeight - 1);
            var bands = raw is { } result ? ReadBands(result.Data, metadata, (row * metadata.TileWidth) + column) : null;
            return bands is null ? null : Pixel(registration, Request, bands);
        }

        // A COG whose read fails is skipped (and logged), as the tile route skips it.
        private async Task<RasterResult?> TryReadRawAsync(CogRegistration registration, ImageServerCogExport.TileAddress tile, CancellationToken cancellationToken)
        {
            try
            {
                return await Resolver.GetTileAsync(registration, tile.Z, tile.Y, tile.X, RasterFormat.Raw, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
            {
                ImageServerLog.CogTileReadSkipped(Logger, ex, registration.Id, tile.Z, tile.Y, tile.X);
                return null;
            }
        }
    }

    private static ImageServerCogExport.TileAddress TileAt(double x, double y, int zoom)
    {
        var count = 1 << zoom;
        var span = ImageServerCogExport.WorldSpan / count;
        var origin = ImageServerCogExport.WorldSpan / 2;
        return new ImageServerCogExport.TileAddress(
            zoom,
            Math.Clamp((int)Math.Floor((x + origin) / span), 0, count - 1),
            Math.Clamp((int)Math.Floor((origin - y) / span), 0, count - 1));
    }

    // Samples are chunky (band-interleaved) in the file's byte order, as the tile decoder leaves them.
    internal static double[]? ReadBands(byte[] samples, CogMetadata metadata, int pixelIndex)
    {
        var bytesPerSample = metadata.BitsPerSample / 8;
        var offset = (long)pixelIndex * metadata.BandCount * bytesPerSample;
        if (bytesPerSample is not (1 or 2 or 4 or 8) || offset + ((long)metadata.BandCount * bytesPerSample) > samples.Length)
        {
            return null;
        }

        return Enumerable.Range(0, metadata.BandCount)
            .Select(band => ReadSample(samples.AsSpan((int)offset + (band * bytesPerSample), bytesPerSample), metadata))
            .ToArray();
    }

    private static double ReadSample(ReadOnlySpan<byte> sample, CogMetadata metadata)
    {
        Span<byte> ordered = stackalloc byte[sample.Length];
        sample.CopyTo(ordered);
        if (metadata.IsLittleEndian != BitConverter.IsLittleEndian)
        {
            ordered.Reverse();
        }

        return metadata.PixelType switch
        {
            "uint8" => ordered[0],
            "int8" => (sbyte)ordered[0],
            "uint16" => BitConverter.ToUInt16(ordered),
            "int16" => BitConverter.ToInt16(ordered),
            "uint32" => BitConverter.ToUInt32(ordered),
            "int32" => BitConverter.ToInt32(ordered),
            "float32" => BitConverter.ToSingle(ordered),
            "float64" => BitConverter.ToDouble(ordered),
            _ => double.NaN,
        };
    }

    // Bands are numbered from 1 and a band holding the COG's NoData (or NaN) is null, as raster_data
    // identify reports them; the pixel has data when any band does.
    private static PixelValueResult Pixel(CogRegistration registration, ImageServerCogIdentifyRequest request, double[] bands)
    {
        var noData = double.TryParse(registration.Metadata!.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
        var values = bands
            .Select((sample, band) => (Band: band + 1, Value: double.IsNaN(sample) || sample.Equals(noData) ? null : (object?)sample))
            .ToDictionary(pair => pair.Band, pair => pair.Value);
        var hasData = values.Values.Any(sample => sample is not null);
        return new PixelValueResult
        {
            X = request.X,
            Y = request.Y,
            Srid = 3857,
            HasData = hasData,
            BandValues = values,
            ContributingRasterIds = hasData ? [registration.Id] : [],
        };
    }
}

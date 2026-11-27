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
    // its NoData document.
    public static async Task<ImageServerCogIdentifyResult?> TryReadAsync(
        HttpContext context, ImageServerCogIdentifyRequest request, CancellationToken cancellationToken)
    {
        var points = await ImageServerCogPoints.OpenAsync(context, request.Layer, cancellationToken).ConfigureAwait(false);
        if (points is null)
        {
            return null;
        }

        return points.Refuse(request) is { } refusal
            ? new ImageServerCogIdentifyResult(null, null, refusal)
            : await points.ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }

    // An absent sr is the service's own reference, Web Mercator for these layers. The reference is
    // checked before coverage, because a point in another reference never lands inside a Web
    // Mercator extent.
    internal static string? Unsupported(ImageServerCogIdentifyRequest request)
        => !ImageServerCogExport.IsWebMercator(request.Srid)
            ? "Identify on cloud COG layers takes a Web Mercator point; use sr 3857 (102100 is the same system)."
            : request.HasRenderingRule
                ? "renderingRule is not applied to cloud COG layers; identify reports their raw samples."
                : null;

    // The first COG holding data at the point wins, as export draws it; when every covering COG
    // holds NoData there, the first one reports it.
    internal static async Task<ImageServerCogIdentifyResult?> ReadFirstWithDataAsync(
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

    internal static bool Covers(CogMetadata metadata, ImageServerCogIdentifyRequest request)
        => metadata.Srid == 3857 && metadata.Compression != "JPEG" && metadata.PlanarConfiguration == 1
            && request.X >= metadata.Extent.XMin && request.X < metadata.Extent.XMax
            && request.Y > metadata.Extent.YMin && request.Y <= metadata.Extent.YMax;

    internal sealed record CogPixelReader(ICogTileResolver Resolver, ImageServerCogIdentifyRequest Request)
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

    internal readonly record struct BandReadSpec(CogMetadata Metadata, int PixelIndex);

    // Samples are chunky (band-interleaved) in the file's byte order, as the tile decoder leaves them.
    internal static double[]? ReadBands(byte[] samples, CogMetadata metadata, int pixelIndex)
    {
        var result = new double[metadata.BandCount];
        return TryReadBands(samples, new BandReadSpec(metadata, pixelIndex), result) ? result : null;
    }

    internal static bool TryReadBands(ReadOnlySpan<byte> samples, BandReadSpec spec, Span<double> destination)
    {
        var metadata = spec.Metadata;
        var bytesPerSample = metadata.BitsPerSample / 8;
        var bandCount = metadata.BandCount;
        var offset = (long)spec.PixelIndex * bandCount * bytesPerSample;
        if (bytesPerSample is not (1 or 2 or 4 or 8)
            || destination.Length < bandCount
            || offset < 0
            || offset + ((long)bandCount * bytesPerSample) > samples.Length)
        {
            return false;
        }

        var baseOffset = (int)offset;
        for (var band = 0; band < bandCount; band++)
        {
            destination[band] = ReadSample(samples.Slice(baseOffset + (band * bytesPerSample), bytesPerSample), metadata);
        }

        return true;
    }

    internal static bool IsExactMatch(double left, double right)
        => BitConverter.DoubleToInt64Bits(left + 0d) == BitConverter.DoubleToInt64Bits(right + 0d);

    internal static bool TryParseNoData(CogMetadata metadata, out double noData)
    {
        if (!double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            noData = 0;
            return false;
        }

        // A float32 NoData is compared at float32 precision, as the decoded samples carry it.
        noData = metadata.PixelType == "float32" ? (float)value : value;
        return double.IsFinite(noData);
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

    // Bands are numbered from 1 and a band holding the COG's NoData (or non-finite sample) is null,
    // as raster_data identify reports them; the pixel has data when any band does.
    private static PixelValueResult Pixel(CogRegistration registration, ImageServerCogIdentifyRequest request, double[] bands)
    {
        var hasNoData = TryParseNoData(registration.Metadata!, out var noData);
        var values = new Dictionary<int, object?>(bands.Length);
        var hasData = false;
        for (var band = 0; band < bands.Length; band++)
        {
            var sample = bands[band];
            var isMissing = !double.IsFinite(sample) || (hasNoData && IsExactMatch(sample, noData));
            values[band + 1] = isMissing ? null : sample;
            hasData |= !isMissing;
        }

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

// The COG-only registrations of a layer, resolved once per request so getSamples can read many
// points without listing them again for each one.
internal sealed class ImageServerCogPoints
{
    private readonly HttpContext _context;
    private readonly ICogTileResolver _resolver;
    private readonly CogRegistration[] _registrations;
    private readonly ILogger _logger;

    private ImageServerCogPoints(HttpContext context, ICogTileResolver resolver, CogRegistration[] registrations)
    {
        _context = context;
        _resolver = resolver;
        _registrations = registrations;
        _logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageServerCogIdentify).FullName!)
            ?? NullLogger.Instance;
    }

    // null when the layer is not COG-only (or COG serving is not installed).
    public static async Task<ImageServerCogPoints?> OpenAsync(HttpContext context, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        var resolver = context.RequestServices.GetService<ICogTileResolver>();
        var registrations = resolver is null
            ? []
            : await ImageServerCogRasters.ListCogOnlyAsync(context, layer, cancellationToken).ConfigureAwait(false);
        return registrations.Length == 0 ? null : new ImageServerCogPoints(context, resolver!, registrations);
    }

    // 501 for a reference or rendering the COG samples cannot answer, 402 without the entitlement.
    public IResult? Refuse(ImageServerCogIdentifyRequest request)
        => ImageServerCogIdentify.Unsupported(request) is { } unsupported
            ? StandardErrorHelpers.CreateNotImplemented(_context, unsupported)
            : ImageServerCogRasters.IsEntitled(_context) ? null : ImageServerCogRasters.PaymentRequired(_context);

    // null when no readable COG covers the point.
    public async Task<ImageServerCogIdentifyResult?> ReadAsync(ImageServerCogIdentifyRequest request, CancellationToken cancellationToken)
    {
        var covering = _registrations.Where(registration => ImageServerCogIdentify.Covers(registration.Metadata!, request)).ToArray();
        return covering.Length == 0
            ? null
            : await ImageServerCogIdentify.ReadFirstWithDataAsync(
                new ImageServerCogIdentify.CogPixelReader(_resolver, request) { Logger = _logger }, covering, cancellationToken).ConfigureAwait(false);
    }
}

// One getSamples point in its own reference.
internal readonly record struct ImageServerCogSamplePoint(double X, double Y, int? Srid);

// Samples many points of one request from a COG-only layer. The registrations are resolved on the
// first point that raster_data does not answer, so an ordinary raster_data layer never reaches the
// COG store, and each point's own reference is checked.
internal sealed class ImageServerCogSampler(HttpContext context, ImageServerCogLayer layer)
{
    private bool _opened;
    private ImageServerCogPoints? _points;

    // null when the layer is not COG-only or no readable COG covers the point.
    public async Task<ImageServerCogIdentifyResult?> ReadAsync(ImageServerCogSamplePoint point, CancellationToken cancellationToken)
    {
        if (!_opened)
        {
            _points = await ImageServerCogPoints.OpenAsync(context, layer, cancellationToken).ConfigureAwait(false);
            _opened = true;
        }

        var request = new ImageServerCogIdentifyRequest(layer, point.X, point.Y) { Srid = point.Srid };
        if (_points is null)
        {
            return null;
        }

        return _points.Refuse(request) is { } refusal
            ? new ImageServerCogIdentifyResult(null, null, refusal)
            : await _points.ReadAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

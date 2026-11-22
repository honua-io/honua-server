// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Tiles;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Rendering;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using SkiaSharp;
using static Honua.Infrastructure.Rendering.RasterMapRenderingPipeline;

namespace Honua.Protocols.Ogc.Classic;

internal static partial class OgcClassicRasterSupport
{
    private enum RasterTileGridMode
    {
        WebMercatorQuad,
        Windowed
    }

    internal readonly record struct RasterTileRequest
    {
        public required int StorageLayerId { get; init; }
        public int? PublicationLayerIndex { get; init; }
        public required string PublicationId { get; init; }
        public MetadataV2GraphSnapshot? Snapshot { get; init; }
        public required int TileMatrix { get; init; }
        public required int TileRow { get; init; }
        public required int TileCol { get; init; }
        public TileGridKind? BuiltInGrid { get; init; }
        public GridGeometry? CustomGrid { get; init; }
    }

    private readonly record struct TileGridSpec
    {
        public required TileBounds Bounds { get; init; }
        public required int Srid { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required RasterTileGridMode Mode { get; init; }
    }

    private readonly record struct PostGisTileFetch
    {
        public required IRasterStore RasterStore { get; init; }
        public required RasterTileRequest Request { get; init; }
        public required RasterInfo[] SelectedRasters { get; init; }
        public required TileGridSpec TileSpec { get; init; }
    }

    internal static async Task<RasterTileRenderResult> RenderRasterTileAsync(
        HttpContext context,
        RasterTileRequest request,
        CancellationToken cancellationToken)
    {
        var tileSpec = ResolveTileGridSpec(request);
        await using var renderLease = await context.RequestServices
            .GetRequiredService<RasterRenderCapacityLimiter>()
            .TryAcquireAsync(tileSpec.Width, tileSpec.Height, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is null)
        {
            return RasterTileRenderResult.Failure(StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds));
        }

        var postgisTile = await TryRenderPostGisTileAsync(context, (request, tileSpec), cancellationToken)
            .ConfigureAwait(false);
        if (postgisTile is not null)
        {
            return postgisTile.Value;
        }

        if (tileSpec.Mode == RasterTileGridMode.WebMercatorQuad)
        {
            var cogTile = await TryRenderCogTileAsync(context, request, cancellationToken).ConfigureAwait(false);
            if (cogTile is not null)
            {
                return cogTile.Value;
            }
        }

        return CreateTransparentTile(tileSpec.Width, tileSpec.Height);
    }

    private static TileGridSpec ResolveTileGridSpec(RasterTileRequest request)
    {
        if (request.CustomGrid is { } custom)
        {
            var bounds = custom.GetTileBounds(request.TileCol, request.TileRow, request.TileMatrix)
                ?? throw new ArgumentOutOfRangeException(nameof(request), "Requested level is not part of the tile matrix set.");
            return new TileGridSpec
            {
                Bounds = bounds,
                Srid = custom.Srid,
                Width = custom.TileWidth,
                Height = custom.TileHeight,
                Mode = RasterTileGridMode.Windowed
            };
        }

        if (request.BuiltInGrid == TileGridKind.WorldCrs84Quad)
        {
            return new TileGridSpec
            {
                Bounds = TileMath.GetTileBoundsGeographic(request.TileCol, request.TileRow, request.TileMatrix),
                Srid = GeographicTileSrid,
                Width = TileSize,
                Height = TileSize,
                Mode = RasterTileGridMode.Windowed
            };
        }

        return new TileGridSpec
        {
            Bounds = TileMath.GetTileBounds(request.TileCol, request.TileRow, request.TileMatrix),
            Srid = TileSrid,
            Width = TileSize,
            Height = TileSize,
            Mode = RasterTileGridMode.WebMercatorQuad
        };
    }

    private static async Task<RasterTileRenderResult?> TryRenderPostGisTileAsync(
        HttpContext context,
        (RasterTileRequest Request, TileGridSpec TileSpec) tileContext,
        CancellationToken cancellationToken)
    {
        var rasterStore = context.RequestServices.GetService<IRasterStore>();
        if (rasterStore is null)
        {
            return null;
        }

        var bounds = tileContext.TileSpec.Bounds;
        var envelopeWkb = CreateEnvelopeWkb(new Envelope(bounds.XMin, bounds.XMax, bounds.YMin, bounds.YMax));
        var selectedRasters = await rasterStore.QueryRastersAsync(
            tileContext.Request.StorageLayerId,
            new RasterSelectionQuery { Geometry = envelopeWkb, GeometrySrid = tileContext.TileSpec.Srid },
            cancellationToken).ConfigureAwait(false);

        if (selectedRasters.Length == 0)
        {
            return null;
        }

        var fetch = new PostGisTileFetch
        {
            RasterStore = rasterStore,
            Request = tileContext.Request,
            SelectedRasters = selectedRasters,
            TileSpec = tileContext.TileSpec
        };
        var tileResult = tileContext.TileSpec.Mode == RasterTileGridMode.WebMercatorQuad
            ? await FetchWebMercatorTileAsync(fetch, cancellationToken).ConfigureAwait(false)
            : await FetchWindowedTileAsync(fetch, cancellationToken).ConfigureAwait(false);

        return tileResult is { Data.Length: > 0 }
            ? RasterTileRenderResult.Success(tileResult.Value.Data, selectedRasters.Length)
            : null;
    }

    private static Task<RasterResult?> FetchWebMercatorTileAsync(
        PostGisTileFetch fetch,
        CancellationToken cancellationToken)
        => fetch.SelectedRasters.Length == 1
            ? fetch.RasterStore.GetImageTileAsync(
                fetch.Request.StorageLayerId,
                fetch.SelectedRasters[0].Id,
                fetch.Request.TileMatrix,
                fetch.Request.TileRow,
                fetch.Request.TileCol,
                RasterFormat.PNG,
                cancellationToken)
            : fetch.RasterStore.GetMosaicImageTileAsync(
                fetch.Request.StorageLayerId,
                fetch.SelectedRasters.Select(static r => r.Id).ToArray(),
                RasterMergeStrategy.Newest,
                fetch.Request.TileMatrix,
                fetch.Request.TileRow,
                fetch.Request.TileCol,
                RasterFormat.PNG,
                cancellationToken);

    private static Task<RasterResult?> FetchWindowedTileAsync(
        PostGisTileFetch fetch,
        CancellationToken cancellationToken)
    {
        var window = new RasterTileWindow
        {
            MinX = fetch.TileSpec.Bounds.XMin,
            MinY = fetch.TileSpec.Bounds.YMin,
            MaxX = fetch.TileSpec.Bounds.XMax,
            MaxY = fetch.TileSpec.Bounds.YMax,
            Srid = fetch.TileSpec.Srid,
            TileWidth = fetch.TileSpec.Width,
            TileHeight = fetch.TileSpec.Height
        };

        return fetch.SelectedRasters.Length == 1
            ? fetch.RasterStore.GetImageTileAsync(
                fetch.Request.StorageLayerId,
                fetch.SelectedRasters[0].Id,
                window,
                RasterFormat.PNG,
                cancellationToken)
            : fetch.RasterStore.GetMosaicImageTileAsync(
                fetch.Request.StorageLayerId,
                fetch.SelectedRasters.Select(static r => r.Id).ToArray(),
                RasterMergeStrategy.Newest,
                window,
                RasterFormat.PNG,
                cancellationToken);
    }

    private static async Task<RasterTileRenderResult?> TryRenderCogTileAsync(
        HttpContext context,
        RasterTileRequest request,
        CancellationToken cancellationToken)
    {
        var cogResolver = context.RequestServices.GetService<ICogTileResolver>();
        if (cogResolver is null ||
            request.Snapshot is null ||
            request.PublicationLayerIndex is not { } publicationLayerIndex ||
            CogPublicationBinding.Resolve(request.Snapshot, publicationLayerIndex)?.Metadata.Id != request.PublicationId)
        {
            return null;
        }

        var lookup = await cogResolver.GetTileForLayerAsync(
            publicationLayerIndex,
            request.TileMatrix,
            request.TileRow,
            request.TileCol,
            RasterFormat.PNG,
            cancellationToken).ConfigureAwait(false);

        if (lookup.EditionGateHit)
        {
            return RasterTileRenderResult.Failure(StandardErrorHelpers.CreatePaymentRequired(
                context,
                "COG Serving requires an active Pro entitlement. Install a license that includes 'raster.cloud-cog-serving'.",
                ["entitlement: raster.cloud-cog-serving"]));
        }

        return lookup.Result is { Data.Length: > 0 }
            ? RasterTileRenderResult.Success(lookup.Result.Value.Data, 1)
            : null;
    }

    private static RasterTileRenderResult CreateTransparentTile(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var bytes = SkiaMapRenderer.EncodeSurface(surface, "png");
        return RasterTileRenderResult.Success(bytes, 0);
    }
}

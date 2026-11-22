// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Rendering;
using Microsoft.Extensions.DependencyInjection;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SkiaSharp;

namespace Honua.Protocols.Ogc.Classic;

internal static partial class OgcClassicRasterSupport
{
    internal readonly record struct ResourceExtentLookup(
        MetadataV2Object Resource,
        int StorageLayerId);

    internal readonly record struct RasterMapRenderInput
    {
        public required int StorageLayerId { get; init; }
        public required SkiaMapRenderer.RenderExtent Extent { get; init; }
        public required int RequestSrid { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
    }

    internal readonly record struct ResolvedExtent
    {
        public required double West { get; init; }
        public required double South { get; init; }
        public required double East { get; init; }
        public required double North { get; init; }
        public required int Srid { get; init; }
    }

    internal static bool IsRasterResource(MetadataV2Object resource)
        => resource.Type == MetadataV2ObjectType.RasterDataset;

    internal static bool ResourceHasGeometry(MetadataV2Object resource)
        => resource.ReadGeometryType() != MetadataV2GeometryType.None
            || resource.FindPrimaryGeometryField() is not null;

    internal static bool IsRenderableResource(MetadataV2Object resource)
        => ResourceHasGeometry(resource) || IsRasterResource(resource);

    internal static async Task<ResolvedExtent?> TryResolveResourceExtentAsync(
        HttpContext context,
        ResourceExtentLookup lookup,
        CancellationToken cancellationToken)
    {
        var bbox = lookup.Resource.ReadBbox();
        if (bbox is not null)
        {
            return new ResolvedExtent
            {
                West = bbox.West,
                South = bbox.South,
                East = bbox.East,
                North = bbox.North,
                Srid = lookup.Resource.ReadSrid() ?? 4326
            };
        }

        if (!IsRasterResource(lookup.Resource) ||
            context.RequestServices.GetService<IRasterStore>() is not { } rasterStore)
        {
            return null;
        }

        var info = await rasterStore.GetPrimaryRasterInfoAsync(lookup.StorageLayerId, cancellationToken)
            .ConfigureAwait(false);
        return info?.Extent is { } extent
            ? new ResolvedExtent
            {
                West = extent.XMin,
                South = extent.YMin,
                East = extent.XMax,
                North = extent.YMax,
                Srid = extent.Srid ?? lookup.Resource.ReadSrid() ?? 4326
            }
            : null;
    }

    internal static async Task<byte[]?> TryRenderRasterMapLayerAsync(
        HttpContext context,
        RasterMapRenderInput input,
        CancellationToken cancellationToken)
    {
        var rasterStore = context.RequestServices.GetService<IRasterStore>();
        if (rasterStore is null)
        {
            return null;
        }

        var envelopeWkb = CreateEnvelopeWkb(new Envelope(
            input.Extent.MinX, input.Extent.MaxX, input.Extent.MinY, input.Extent.MaxY));
        var selectedRasters = await rasterStore.QueryRastersAsync(
            input.StorageLayerId,
            new RasterSelectionQuery { Geometry = envelopeWkb, GeometrySrid = input.RequestSrid },
            cancellationToken).ConfigureAwait(false);

        if (selectedRasters.Length == 0)
        {
            return null;
        }

        var rasterQuery = BuildMapExportQuery(input, envelopeWkb);
        var result = await ExportSelectedRastersAsync(
            rasterStore,
            (input.StorageLayerId, selectedRasters, rasterQuery),
            cancellationToken).ConfigureAwait(false);

        return result.Data.Length > 0 ? result.Data : null;
    }

    internal static void DrawEncodedRaster(SKCanvas canvas, byte[] imageBytes, SKRect destination)
    {
        using var image = SKImage.FromEncodedData(imageBytes);
        if (image is null)
        {
            return;
        }

        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
    }

    private static RasterQuery BuildMapExportQuery(RasterMapRenderInput input, byte[] envelopeWkb)
        => new()
        {
            ClipRegion = new RasterClipRegion { Geometry = envelopeWkb, Srid = input.RequestSrid },
            CoverClipExtent = true,
            OutputSrid = input.RequestSrid,
            OutputWidth = input.Width,
            OutputHeight = input.Height,
            OutputFormat = RasterFormat.PNG,
            ResamplingAlgorithm = ResamplingAlgorithm.Bilinear
        };

    private static Task<RasterResult> ExportSelectedRastersAsync(
        IRasterStore rasterStore,
        (int StorageLayerId, RasterInfo[] SelectedRasters, RasterQuery Query) export,
        CancellationToken cancellationToken)
        => export.SelectedRasters.Length == 1
            ? rasterStore.ExportImageAsync(
                export.StorageLayerId,
                export.SelectedRasters[0].Id,
                export.Query,
                cancellationToken)
            : rasterStore.ExportMosaicAsync(
                export.StorageLayerId,
                export.SelectedRasters.Select(static r => r.Id).ToArray(),
                RasterMergeStrategy.Newest,
                export.Query,
                RasterMosaicOrdering.AcquisitionNewest,
                cancellationToken: cancellationToken);

    private static byte[] CreateEnvelopeWkb(Envelope envelope)
    {
        var geometry = new GeometryFactory().ToGeometry(envelope);
        return new WKBWriter().Write(geometry);
    }
}

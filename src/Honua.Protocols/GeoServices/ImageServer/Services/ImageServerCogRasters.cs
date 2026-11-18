// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Honua.Infrastructure.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// The publication whose service document is being described, in the snapshot it was resolved
// from, with the raster_data rasters already found for it.
internal sealed record ImageServerCogLayer(MetadataV2GraphSnapshot Snapshot, MetadataV2Layer Publication, RasterInfo[] PostgisRasters);

// The rasters a COG-only layer is described from, or the refusal its tiles would also give.
internal sealed record ImageServerCogResolution(RasterInfo[] Rasters, IResult? Refusal)
{
    public bool FromCogs { get; init; }
}

// Describes a layer whose imagery is only registered cloud COGs as rasters, so the service
// document reports the COG grid the tile route serves. Registrations bind by publication layer
// index exactly as the tile fallback does, and only scanned registrations have a grid to report.
internal static class ImageServerCogRasters
{
    public const string Entitlement = "raster.cloud-cog-serving";

    // exportImage composes the COG tiles and identify and getSamples read their samples (the Image
    // capability); statistics and histograms read raster_data only, so they are not advertised.
    public const string Capabilities = "Image,Metadata";

    // raster_data rasters win; COGs describe the layer only when it has none. Registered COG ids
    // are not raster_data rows, so the caller skips raster_data statistics when FromCogs is set.
    public static async Task<ImageServerCogResolution> ResolveAsync(
        HttpContext context, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        if (layer.PostgisRasters.Length > 0)
        {
            return new ImageServerCogResolution(layer.PostgisRasters, null);
        }

        var rasters = await ListAsync(context, layer, cancellationToken).ConfigureAwait(false);
        var refusal = rasters.Length > 0 && !IsEntitled(context) ? PaymentRequired(context) : null;
        return new ImageServerCogResolution(rasters, refusal) { FromCogs = true };
    }

    public static async Task<RasterInfo[]> ListAsync(HttpContext context, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        var registrations = await ListScannedAsync(context, layer, cancellationToken).ConfigureAwait(false);
        var rasters = registrations.Select(Describe).ToArray();

        // One service envelope cannot union grids in different CRSs; keep the first registration's.
        return rasters.Length == 0 ? rasters : rasters.Where(raster => raster.Srid == rasters[0].Srid).ToArray();
    }

    // The scanned registrations bound to the publication, in store order (the tile route's order).
    public static async Task<CogRegistration[]> ListScannedAsync(
        HttpContext context, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        var cogStore = context.RequestServices.GetService<ICogStore>();
        if (cogStore is null
            || layer.Publication.LayerIndex is not { } layerIndex
            || CogPublicationBinding.Resolve(layer.Snapshot, layerIndex)?.Metadata.Id != layer.Publication.Metadata.Id)
        {
            return [];
        }

        var registrations = await cogStore.ListByLayerAsync(layerIndex, cancellationToken).ConfigureAwait(false);
        return registrations.Where(registration => registration.Metadata is { Width: > 0, Height: > 0 }).ToArray();
    }

    // The scanned registrations of a layer holding no raster_data rows. raster_data wins, as in
    // ResolveAsync, so a layer with both keeps its raster_data answers when a selection is empty.
    public static async Task<CogRegistration[]> ListCogOnlyAsync(
        HttpContext context, ImageServerCogLayer layer, CancellationToken cancellationToken)
    {
        var registrations = await ListScannedAsync(context, layer, cancellationToken).ConfigureAwait(false);
        var rasterStore = context.RequestServices.GetService<IRasterStore>();
        if (registrations.Length == 0 || rasterStore is null
            || (layer.Snapshot.ResolveStorageLayerId(layer.Publication) ?? layer.Publication.LayerIndex) is not { } storageLayerId)
        {
            return registrations;
        }

        var primary = await rasterStore.GetPrimaryRasterInfoAsync(storageLayerId, cancellationToken).ConfigureAwait(false);
        return primary is null ? registrations : [];
    }

    public static IResult PaymentRequired(HttpContext context)
        => StandardErrorHelpers.CreatePaymentRequired(
            context,
            "COG Serving requires an active Pro entitlement. Install a license that includes 'raster.cloud-cog-serving'.",
            ["entitlement: " + Entitlement]);

    // Matches the tile route, which answers 402 for these layers without the entitlement.
    public static bool IsEntitled(HttpContext context)
        => context.RequestServices.GetService<ILicenseEntitlementService>()?.CheckEntitlement(Entitlement).IsActive ?? true;

    // GeoTransform is north-up: the tile path serves only axis-aligned EPSG:3857 COGs.
    internal static RasterInfo Describe(CogRegistration registration)
    {
        var metadata = registration.Metadata ?? throw new ArgumentException("The COG has not been scanned.", nameof(registration));
        int? srid = metadata.Extent.Srid is > 0 ? metadata.Extent.Srid : metadata.Srid > 0 ? metadata.Srid : null;
        var extent = metadata.Extent with { Srid = srid };
        var noData = double.TryParse(metadata.NoData, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : (double?)null;
        return new RasterInfo
        {
            Id = registration.Id,
            LayerId = registration.LayerId,
            Name = registration.Name,
            Width = metadata.Width,
            Height = metadata.Height,
            BandCount = metadata.BandCount,
            PixelType = ToPostgisPixelType(metadata.PixelType),
            Srid = srid,
            NoDataValue = noData,
            HasUniformNoDataValue = noData is not null,
            Extent = extent,
            GeoTransform =
            [
                extent.XMin, (extent.XMax - extent.XMin) / metadata.Width, 0,
                extent.YMax, 0, -(extent.YMax - extent.YMin) / metadata.Height,
            ],
            CreatedAt = registration.CreatedAt,
            ModifiedAt = registration.MetadataScannedAt,
        };
    }

    // The COG scanner names sample types uint8/int16/float32; service info maps PostGIS codes.
    private static string ToPostgisPixelType(string pixelType) => pixelType switch
    {
        "uint8" => "8BUI",
        "int8" => "8BSI",
        "uint16" => "16BUI",
        "int16" => "16BSI",
        "uint32" => "32BUI",
        "int32" => "32BSI",
        "float32" => "32BF",
        "float64" => "64BF",
        _ => pixelType,
    };
}

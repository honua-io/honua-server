// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// Describes a layer whose imagery is only registered cloud COGs as rasters, so the service
// document reports the COG grid the tile route serves. Registrations bind by publication layer
// index exactly as the tile fallback does, and only scanned registrations have a grid to report.
internal static class ImageServerCogRasters
{
    public static async Task<RasterInfo[]> ListAsync(
        HttpContext context,
        MetadataV2GraphSnapshot snapshot,
        MetadataV2Layer publication)
    {
        var cogStore = context.RequestServices.GetService<ICogStore>();
        if (cogStore is null
            || publication.LayerIndex is not { } layerIndex
            || CogPublicationBinding.Resolve(snapshot, layerIndex)?.Metadata.Id != publication.Metadata.Id)
        {
            return [];
        }

        var registrations = await cogStore.ListByLayerAsync(layerIndex, context.RequestAborted).ConfigureAwait(false);
        return registrations
            .Where(registration => registration.Metadata is { Width: > 0, Height: > 0 })
            .Select(Describe)
            .ToArray();
    }

    internal static RasterInfo Describe(CogRegistration registration)
    {
        var metadata = registration.Metadata ?? throw new ArgumentException("The COG has not been scanned.", nameof(registration));
        var extent = metadata.Extent with { Srid = metadata.Extent.Srid ?? metadata.Srid };
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
            PixelType = metadata.PixelType,
            Srid = metadata.Srid,
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
}

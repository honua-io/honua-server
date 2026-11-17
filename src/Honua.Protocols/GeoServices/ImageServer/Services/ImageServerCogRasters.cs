// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

// A layer whose pixels come only from registered cloud COGs (the ImageServer tile fallback) is
// described from the scanned COG metadata. The binding rule is the tile handler's, so service
// info never describes a COG the tile route would not serve for this publication.
internal static class ImageServerCogRasters
{
    public static async Task<RasterInfo[]> ListAsync(
        ICogStore? cogStore,
        (MetadataV2GraphSnapshot Snapshot, MetadataV2Layer Publication) binding,
        CancellationToken cancellationToken)
    {
        if (cogStore is null
            || binding.Publication.LayerIndex is not { } layerIndex
            || CogPublicationBinding.Resolve(binding.Snapshot, layerIndex)?.Metadata.Id != binding.Publication.Metadata.Id)
        {
            return [];
        }

        var registrations = await cogStore.ListByLayerAsync(layerIndex, cancellationToken).ConfigureAwait(false);
        var rasters = new List<RasterInfo>(registrations.Length);
        foreach (var registration in registrations)
        {
            if (registration.Metadata is { Width: > 0, Height: > 0 } metadata)
            {
                rasters.Add(ToRasterInfo(registration, metadata));
            }
        }

        return [.. rasters];
    }

    private static RasterInfo ToRasterInfo(CogRegistration registration, CogMetadata metadata)
    {
        var extent = metadata.Extent;
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
            Srid = metadata.Srid > 0 ? metadata.Srid : null,
            PixelType = ToPostgisPixelType(metadata.PixelType),
            NoDataValue = noData,
            HasUniformNoDataValue = noData.HasValue,
            GeoTransform =
            [
                extent.XMin, (extent.XMax - extent.XMin) / metadata.Width, 0,
                extent.YMax, 0, -(extent.YMax - extent.YMin) / metadata.Height
            ],
            Extent = extent with { Srid = metadata.Srid > 0 ? metadata.Srid : extent.Srid },
            CreatedAt = registration.CreatedAt,
            ModifiedAt = registration.MetadataScannedAt
        };
    }

    // COG pixel types are "<uint|int|float><bits>"; the service descriptor maps PostGIS names.
    internal static string ToPostgisPixelType(string cogPixelType) => cogPixelType switch
    {
        "float32" => "32BF",
        "float64" => "64BF",
        "uint1" => "1BB",
        _ when cogPixelType.StartsWith("uint", StringComparison.Ordinal) => cogPixelType[4..] + "BUI",
        _ when cogPixelType.StartsWith("int", StringComparison.Ordinal) => cogPixelType[3..] + "BSI",
        _ => "8BUI"
    };
}

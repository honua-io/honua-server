// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

internal static class ImageServerCatalogTime
{
    public static ImageServerCatalogTimeFields? Resolve(
        (string? StartTimeField, string? EndTimeField, string? TrackIdField) hints,
        IReadOnlyList<RasterInfo> rasters,
        ImmutableArray<string> maskedFields = default)
    {
        ArgumentNullException.ThrowIfNull(rasters);

        var schema = ImageServerCatalogFields.Schema(maskedFields);
        var hasAcquisitionDates = rasters.Any(static raster => raster.AcquisitionDate.HasValue);
        var extent = hasAcquisitionDates ? ImageServerMosaicHelpers.CreateTimeExtent(rasters) : null;
        var start = ResolveField(schema, hints.StartTimeField, dateOnly: true)
            ?? (extent is null ? null : ResolveField(schema, "AcquisitionDate", dateOnly: true));

        // The end field is optional interval metadata attached to a start field;
        // an end-only definition is not a usable time capability.
        if (start is null)
        {
            return null;
        }

        return new ImageServerCatalogTimeFields(
            start,
            ResolveField(schema, hints.EndTimeField, dateOnly: true),
            ResolveField(schema, hints.TrackIdField, dateOnly: false),
            extent);
    }

    private static string? ResolveField(Field[] schema, string? hint, bool dateOnly)
    {
        if (string.IsNullOrWhiteSpace(hint)
            || ImageServerCatalogFields.ResolveCanonicalName(hint) is not { } canonical)
        {
            return null;
        }

        return schema.FirstOrDefault(field =>
                string.Equals(field.Name, canonical, StringComparison.Ordinal)
                && (!dateOnly || string.Equals(field.Type, "esriFieldTypeDate", StringComparison.Ordinal)))
            ?.Name;
    }
}

internal sealed record ImageServerCatalogTimeFields(
    string StartTimeField,
    string? EndTimeField,
    string? TrackIdField,
    long?[]? TimeExtent);

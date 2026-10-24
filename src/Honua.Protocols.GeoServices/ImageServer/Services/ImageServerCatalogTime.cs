// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Resolves the attribute-time fields an Image Server may advertise. REST
/// <c>timeInfo</c> and SOAP <c>GetServiceInfo</c> both read this so they can
/// only name fields that exist in the advertised raster-catalog field list.
/// </summary>
/// <remarks>
/// A V2 resource's <c>temporal</c> hints are authored for the vector/table surface
/// (for example <c>timestamp</c>/<c>event_date</c>). An Image Server exposes the
/// raster catalog instead, so a hint naming a column absent from that catalog is
/// dropped: Esri clients that trust <c>StartTimeFieldName</c> otherwise fail to
/// open the catalog. Multidimensional <c>StdTime</c> support is separate.
/// </remarks>
internal static class ImageServerCatalogTime
{
    /// <summary>
    /// Returns the advertised attribute-time fields and extent, or <c>null</c>
    /// when no catalog date field can serve as the (required) start time field.
    /// </summary>
    /// <param name="hints">The resource's declared temporal field hints.</param>
    /// <param name="rasters">The service rasters, for the acquisition-date fallback and extent.</param>
    /// <param name="maskedFields">Caller field masks; masked fields are never advertised.</param>
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

/// <summary>
/// Attribute-time fields advertised by an Image Server; every name is a catalog field.
/// </summary>
internal sealed record ImageServerCatalogTimeFields(
    string StartTimeField,
    string? EndTimeField,
    string? TrackIdField,
    long?[]? TimeExtent);

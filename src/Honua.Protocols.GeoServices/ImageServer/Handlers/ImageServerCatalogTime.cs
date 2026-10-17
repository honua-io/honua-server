// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Services;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

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
    /// when the catalog carries no usable attribute time.
    /// </summary>
    public static ImageServerCatalogTimeFields? Resolve(
        (string? StartTimeField, string? EndTimeField, string? TrackIdField) hints,
        IReadOnlyList<RasterInfo> rasters)
    {
        ArgumentNullException.ThrowIfNull(rasters);

        var hasAcquisitionDates = rasters.Any(static raster => raster.AcquisitionDate.HasValue);
        var extent = hasAcquisitionDates ? ImageServerMosaicHelpers.CreateTimeExtent(rasters) : null;
        var start = ResolveDateField(hints.StartTimeField) ?? (extent is null ? null : "AcquisitionDate");
        var end = ResolveDateField(hints.EndTimeField);
        if (start is null && end is null)
        {
            return null;
        }

        var trackId = string.IsNullOrWhiteSpace(hints.TrackIdField)
            ? null
            : ImageServerCatalogFields.CanonicalNames.FirstOrDefault(name =>
                string.Equals(name, hints.TrackIdField.Trim(), StringComparison.OrdinalIgnoreCase));
        return new ImageServerCatalogTimeFields(start, end, trackId, extent);
    }

    private static string? ResolveDateField(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return null;
        }

        var trimmed = fieldName.Trim();
        return ImageServerCatalogQueryHandler.BuildCatalogFields()
            .FirstOrDefault(field =>
                string.Equals(field.Type, "esriFieldTypeDate", StringComparison.Ordinal)
                && string.Equals(field.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?.Name;
    }
}

/// <summary>
/// Attribute-time fields advertised by an Image Server; every name is a catalog field.
/// </summary>
internal sealed record ImageServerCatalogTimeFields(
    string? StartTimeField,
    string? EndTimeField,
    string? TrackIdField,
    long?[]? TimeExtent);

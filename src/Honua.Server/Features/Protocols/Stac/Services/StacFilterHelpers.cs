// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Protocols.Stac.Services;

/// <summary>
/// Helpers for converting STAC search parameters to Honua query filters.
/// </summary>
internal static class StacFilterHelpers
{
    private static readonly GeometryFactory Wgs84Factory = new(new PrecisionModel(), 4326);

    [ThreadStatic]
    private static WKBWriter? _wkbWriter;

    private static WKBWriter GetWkbWriter() => _wkbWriter ??= new WKBWriter();
    /// <summary>
    /// Resolves layers that are visible through the STAC protocol, applying both
    /// protocol gating and access-policy filtering.
    /// </summary>
    public static async Task<LayerDefinition[]> ResolveStacVisibleLayersAsync(
        HttpContext context,
        ILayerCatalog layerCatalog,
        CancellationToken cancellationToken)
    {
        var allLayers = await layerCatalog.ListLayersAsync(cancellationToken);
        var services = await layerCatalog.ListServicesAsync(cancellationToken);

        var stacServices = services
            .Where(service => ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.Stac))
            .ToArray();

        var allServices = LayerValidationHelpers.BuildPrimaryServiceMap(services);
        var layerToService = LayerValidationHelpers.BuildPrimaryServiceMap(stacServices, ServiceProtocols.Stac);
        return allLayers
            .Where(layer => layerToService.TryGetValue(layer.Id, out var service)
                ? AccessPolicyHelpers.IsLayerAccessible(context, layer, service)
                : allServices.ContainsKey(layer.Id)
                    ? false
                : ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Stac) &&
                  AccessPolicyHelpers.IsLayerAccessible(context, layer))
            .ToArray();
    }

    /// <summary>
    /// Parses a STAC bbox parameter (comma-separated: west,south,east,north) into a <see cref="SpatialFilter"/>.
    /// </summary>
    public static SpatialFilter? ParseBbox(string bbox)
    {
        var parts = bbox.Split(',');
        if (parts.Length != 4)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var west) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var south) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var east) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var north))
        {
            return null;
        }

        if (!TryValidateBboxCoordinates(west, south, east, north, out _))
        {
            return null;
        }

        return CreateBboxSpatialFilter(west, south, east, north);
    }

    internal static bool TryValidateBboxCoordinates(
        double west,
        double south,
        double east,
        double north,
        out string? error)
    {
        if (!double.IsFinite(west) || !double.IsFinite(south) || !double.IsFinite(east) || !double.IsFinite(north))
        {
            error = "bbox contains a non-finite numeric value.";
            return false;
        }

        if (south > north)
        {
            error = "bbox latitude values are out of range.";
            return false;
        }

        if (south < -90.0 || south > 90.0 || north < -90.0 || north > 90.0)
        {
            error = "bbox latitude values are out of range.";
            return false;
        }

        if (west < -180.0 || west > 180.0 || east < -180.0 || east > 180.0)
        {
            error = "bbox longitude values are out of range.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Creates a <see cref="SpatialFilter"/> from pre-parsed bbox coordinates.
    /// Avoids the string round-trip when the caller already has numeric values.
    /// </summary>
    public static SpatialFilter CreateBboxSpatialFilter(double west, double south, double east, double north)
    {
        Geometry geometry;
        if (west > east)
        {
            var eastHemisphere = CreateBboxPolygon(west, south, 180.0, north);
            var westHemisphere = CreateBboxPolygon(-180.0, south, east, north);
            geometry = Wgs84Factory.CreateMultiPolygon([eastHemisphere, westHemisphere]);
        }
        else
        {
            geometry = Wgs84Factory.ToGeometry(new Envelope(west, east, south, north));
        }

        var wkb = GetWkbWriter().Write(geometry);

        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid: 4326);
    }

    private static Polygon CreateBboxPolygon(double minX, double minY, double maxX, double maxY)
        => Wgs84Factory.CreatePolygon(
            [
                new Coordinate(minX, minY),
                new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY),
                new Coordinate(minX, maxY),
                new Coordinate(minX, minY)
            ]);

    /// <summary>
    /// Parses a GeoJSON geometry from a STAC intersects parameter into a spatial filter.
    /// </summary>
    public static bool TryCreateIntersectsSpatialFilter(
        string? intersectsGeoJson,
        IGeometryService geometryService,
        out SpatialFilter? spatialFilter,
        out string? error)
    {
        spatialFilter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(intersectsGeoJson))
        {
            return true;
        }

        try
        {
            var wkb = geometryService.ConvertGeoJsonToWkb(intersectsGeoJson, srid: 4326);
            if (wkb is null)
            {
                error = "Invalid intersects geometry.";
                return false;
            }

            spatialFilter = SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid: 4326);
            return true;
        }
        catch (ArgumentException)
        {
            error = "Invalid intersects geometry.";
            return false;
        }
    }

    /// <summary>
    /// Parses an RFC 3339 datetime or interval into a <see cref="TemporalFilter"/>.
    /// Supports: instant, ../end, start/.., start/end.
    /// </summary>
    public static TemporalFilter? ParseDatetime(string datetime, LayerDefinition layer)
    {
        var timeField = layer.Metadata?.TimeInfo?.StartTimeField;
        if (string.IsNullOrWhiteSpace(timeField))
        {
            return null;
        }

        var parts = datetime.Split('/');
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (parts.Length == 1)
        {
            // Single instant
            if (DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
            {
                start = instant;
                end = instant;
            }
            else
            {
                return null;
            }
        }
        else if (parts.Length == 2)
        {
            // Interval: start/end, ../end, start/..
            if (!string.Equals(parts[0], "..", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var s))
            {
                start = s;
            }

            if (!string.Equals(parts[1], "..", StringComparison.Ordinal) &&
                DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var e))
            {
                end = e;
            }
        }
        else
        {
            return null;
        }

        if (start is null && end is null)
        {
            return null;
        }

        return new TemporalFilter
        {
            PropertyName = timeField,
            PropertyType = TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };
    }
}

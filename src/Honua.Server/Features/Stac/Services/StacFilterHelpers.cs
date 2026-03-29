// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Stac.Services;

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

        var layerToService = new Dictionary<int, ServiceDefinition>();
        foreach (var service in stacServices)
        {
            foreach (var serviceLayer in service.Layers)
            {
                layerToService.TryAdd(serviceLayer.Id, service);
            }
        }

        var protocolLayerIds = layerToService.Keys.ToHashSet();
        return allLayers
            .Where(layer => protocolLayerIds.Count == 0
                ? ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.Stac)
                : protocolLayerIds.Contains(layer.Id))
            .Where(layer => AccessPolicyHelpers.IsLayerAccessible(
                context, layer, layerToService.GetValueOrDefault(layer.Id)))
            .ToArray();
    }

    /// <summary>
    /// Parses a STAC bbox parameter (comma-separated: west,south,east,north) into a <see cref="SpatialFilter"/>.
    /// </summary>
    public static SpatialFilter? ParseBbox(string bbox)
    {
        var parts = bbox.Split(',');
        if (parts.Length < 4)
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

        return CreateBboxSpatialFilter(west, south, east, north);
    }

    /// <summary>
    /// Creates a <see cref="SpatialFilter"/> from pre-parsed bbox coordinates.
    /// Avoids the string round-trip when the caller already has numeric values.
    /// </summary>
    public static SpatialFilter CreateBboxSpatialFilter(double west, double south, double east, double north)
    {
        var envelope = new Envelope(west, east, south, north);
        var geometry = Wgs84Factory.ToGeometry(envelope);
        var wkb = GetWkbWriter().Write(geometry);

        return SpatialFilter.Create(wkb, SpatialRelationship.Intersects, srid: 4326);
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

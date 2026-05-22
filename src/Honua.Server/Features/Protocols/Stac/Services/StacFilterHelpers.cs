// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
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
    /// Parses a STAC bbox parameter into a <see cref="SpatialFilter"/>.
    /// Supports 2D (west,south,east,north) and 3D (west,south,minZ,east,north,maxZ)
    /// shapes; elevation is validated but not applied to the 2D feature filter.
    /// </summary>
    public static SpatialFilter? ParseBbox(string bbox)
    {
        var parts = bbox.Split(',', StringSplitOptions.None | StringSplitOptions.TrimEntries);
        if (parts.Length is not 4 and not 6)
        {
            return null;
        }

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        if (!TryExtractBbox2D(values, out var west, out var south, out var east, out var north, out _))
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

    internal static bool TryExtractBbox2D(
        IReadOnlyList<double> values,
        out double west,
        out double south,
        out double east,
        out double north,
        out string? error)
    {
        west = south = east = north = 0;

        if (values.Count is not 4 and not 6)
        {
            error = "bbox must contain four or six numeric values.";
            return false;
        }

        west = values[0];
        south = values[1];
        east = values.Count == 6 ? values[3] : values[2];
        north = values.Count == 6 ? values[4] : values[3];

        if (!TryValidateBboxCoordinates(west, south, east, north, out error))
        {
            return false;
        }

        if (values.Count == 6)
        {
            var minZ = values[2];
            var maxZ = values[5];
            if (!double.IsFinite(minZ) || !double.IsFinite(maxZ))
            {
                error = "bbox contains a non-finite numeric value.";
                return false;
            }

            if (minZ > maxZ)
            {
                error = "bbox elevation values are out of range.";
                return false;
            }
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
        var timeField = ResolveTemporalField(layer);
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
            if (TryParseRfc3339DateTime(parts[0], out var instant))
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
            if (!IsOpenIntervalBound(parts[0]))
            {
                if (!TryParseRfc3339DateTime(parts[0], out var s))
                {
                    return null;
                }

                start = s;
            }

            if (!IsOpenIntervalBound(parts[1]))
            {
                if (!TryParseRfc3339DateTime(parts[1], out var e))
                {
                    return null;
                }

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

        if (start > end)
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

    private static string? ResolveTemporalField(LayerDefinition layer)
    {
        var configured = layer.Metadata?.TimeInfo?.StartTimeField;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        ReadOnlySpan<string> fallbackCandidates =
        [
            "datetime", "created_at", "updated_at", "start_datetime",
            "end_datetime", "timestamp", "event_date", "date"
        ];

        foreach (var candidate in fallbackCandidates)
        {
            if (layer.AttributeFields.Any(field =>
                    field.Type is FieldType.Date or FieldType.DateTime &&
                    string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// V2 overload of <see cref="ParseDatetime(string, LayerDefinition)"/>. Reads the
    /// configured temporal field name from <see cref="MetadataV2Resource.Temporal"/>; falls
    /// back to checking <see cref="MetadataV2Resource.SchemaFields"/> for canonical
    /// date/datetime field names.
    /// </summary>
    public static TemporalFilter? ParseDatetime(string datetime, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var timeField = ResolveTemporalField(resource);
        if (string.IsNullOrWhiteSpace(timeField))
        {
            return null;
        }

        return ParseDatetimeWithField(datetime, timeField);
    }

    private static string? ResolveTemporalField(MetadataV2Resource resource)
    {
        var configured = resource.ReadTemporalFields().StartTimeField;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        ReadOnlySpan<string> fallbackCandidates =
        [
            "datetime", "created_at", "updated_at", "start_datetime",
            "end_datetime", "timestamp", "event_date", "date"
        ];

        foreach (var candidate in fallbackCandidates)
        {
            foreach (var field in resource.SchemaFields)
            {
                if (string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    if (field.Type is MetadataV2FieldType.Date
                        or MetadataV2FieldType.DateTime
                        or MetadataV2FieldType.Time)
                    {
                        return candidate;
                    }
                }
            }
        }
        return null;
    }

    private static TemporalFilter? ParseDatetimeWithField(string datetime, string timeField)
    {
        var parts = datetime.Split('/');
        DateTimeOffset? start = null;
        DateTimeOffset? end = null;

        if (parts.Length == 1)
        {
            if (TryParseRfc3339DateTime(parts[0], out var instant))
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
            if (!IsOpenIntervalBound(parts[0]))
            {
                if (!TryParseRfc3339DateTime(parts[0], out var s))
                {
                    return null;
                }
                start = s;
            }
            if (!IsOpenIntervalBound(parts[1]))
            {
                if (!TryParseRfc3339DateTime(parts[1], out var e))
                {
                    return null;
                }
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
        if (start > end)
        {
            return null;
        }

        return new TemporalFilter
        {
            PropertyName = timeField,
            PropertyType = TemporalPropertyType.DateTime,
            Start = start,
            End = end,
        };
    }

    private static bool IsOpenIntervalBound(string value)
        => value.Length == 0 || string.Equals(value, "..", StringComparison.Ordinal);

    private static bool TryParseRfc3339DateTime(string value, out DateTimeOffset timestamp)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            timestamp = default;
            return false;
        }

        if (normalized.Contains(',', StringComparison.Ordinal))
        {
            timestamp = default;
            return false;
        }

        if (normalized.Length > 10 && normalized[10] == 't')
        {
            normalized = normalized[..10] + "T" + normalized[11..];
        }

        if (normalized.EndsWith('z'))
        {
            normalized = normalized[..^1] + "Z";
        }

        var fractionalSeparator = normalized.IndexOf('.', StringComparison.Ordinal);
        if (fractionalSeparator >= 0)
        {
            var offsetStart = normalized.IndexOfAny(['Z', '+', '-'], fractionalSeparator + 1);
            if (offsetStart > fractionalSeparator)
            {
                var fractionalLength = offsetStart - fractionalSeparator - 1;
                if (fractionalLength > 7)
                {
                    normalized = normalized[..(fractionalSeparator + 8)] + normalized[offsetStart..];
                }
            }
        }

        if (!HasExplicitRfc3339TimeZone(normalized))
        {
            timestamp = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);
    }

    private static bool HasExplicitRfc3339TimeZone(string value)
    {
        if (value.Length <= 11 || value[10] != 'T')
        {
            return false;
        }

        if (value.EndsWith('Z'))
        {
            return true;
        }

        var plusOffset = value.LastIndexOf('+');
        var minusOffset = value.LastIndexOf('-');
        var offsetStart = Math.Max(plusOffset, minusOffset);
        return offsetStart > 10 &&
               value.Length - offsetStart == 6 &&
               value[offsetStart + 3] == ':';
    }
}

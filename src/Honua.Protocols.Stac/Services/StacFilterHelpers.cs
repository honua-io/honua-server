// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Filtering;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Protocols.Stac.Services;

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
    /// Validates the syntax of an RFC 3339 datetime or interval string without requiring a
    /// temporal field on the resource.  Returns <see langword="true"/> when the value is
    /// syntactically valid; <see langword="false"/> when the format is wrong.
    /// </summary>
    public static bool IsValidDatetimeSyntax(string datetime)
        => ParseDatetimeWithField(datetime, "_syntax_check_") is not null;

    /// <summary>
    /// Parses an RFC 3339 datetime or interval into a <see cref="TemporalFilter"/> bound to the
    /// resource's temporal field.  Returns <see langword="null"/> when the resource has no
    /// resolvable temporal field (the caller should skip the temporal filter for that layer rather
    /// than treating this as a request error).
    /// </summary>
    /// <remarks>
    /// Use <see cref="IsValidDatetimeSyntax"/> to validate syntax independently of per-resource
    /// field resolution so that a syntactically invalid value can be rejected up front (400) while
    /// a resource without a temporal field is simply not filtered rather than causing a 400 for the
    /// entire search.
    /// </remarks>
    public static TemporalFilter? ParseDatetime(string datetime, MetadataV2Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var timeField = ResolveTemporalField(resource, out var endTimeField);
        if (string.IsNullOrWhiteSpace(timeField))
        {
            // The resource has no resolvable temporal field: the caller must skip the
            // temporal filter for this layer (not return 400).
            return null;
        }

        return ParseDatetimeWithField(datetime, timeField, endTimeField);
    }

    /// <summary>
    /// Resolves the start temporal field for the resource and, when present, a distinct end
    /// temporal field via <paramref name="endTimeField"/>.  When the resource declares an
    /// explicit start time field the configured end time field is propagated (matching the
    /// OGC/GeoServices construction) so interval rows are filtered as
    /// <c>[start, end]</c> rather than as instants.  The fallback schema scan resolves only a
    /// single (instant) field and leaves <paramref name="endTimeField"/> null.
    /// </summary>
    private static string? ResolveTemporalField(MetadataV2Resource resource, out string? endTimeField)
    {
        endTimeField = null;

        var temporal = resource.ReadTemporalFields();
        var configured = temporal.StartTimeField;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Propagate a distinct configured end field so the shared filter pipeline applies
            // interval-intersection semantics ([start, COALESCE(end, start)]). Ignore an end
            // field that matches the start field to preserve instant semantics.
            var configuredEnd = temporal.EndTimeField;
            if (!string.IsNullOrWhiteSpace(configuredEnd) &&
                !string.Equals(configuredEnd, configured, StringComparison.OrdinalIgnoreCase))
            {
                endTimeField = configuredEnd;
            }

            return configured;
        }

        ReadOnlySpan<string> fallbackCandidates = TemporalFieldDefaults.TemporalFallbackFieldNames;

        foreach (var candidate in fallbackCandidates)
        {
            var isTemporalField = resource.SchemaFields.Any(field =>
                string.Equals(field.Name, candidate, StringComparison.OrdinalIgnoreCase)
                && field.Type is MetadataV2FieldType.Date or MetadataV2FieldType.DateTime or MetadataV2FieldType.Time);

            if (isTemporalField)
            {
                return candidate;
            }
        }
        return null;
    }

    private static TemporalFilter? ParseDatetimeWithField(string datetime, string timeField, string? endTimeField = null)
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
            // A doubly-open / empty interval (datetime=../.., /, /.., ../) is not a valid
            // OGC API-Features / STAC datetime value: an interval must have at least one
            // bounded (closed) end. The STAC API conformance validator requires these to be
            // rejected with 400, so return null here to fail the caller's syntax guard.
            // (The earlier BH-S-02 no-op-filter behavior from #2423 accepted them with 200
            // and regressed STAC item-search conformance.)
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
            EndPropertyName = endTimeField,
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

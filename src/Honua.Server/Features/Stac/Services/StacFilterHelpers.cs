// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Stac.Services;

/// <summary>
/// Helpers for converting STAC search parameters to Honua query filters.
/// </summary>
internal static class StacFilterHelpers
{
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

        var envelope = new Envelope(west, east, south, north);
        var geometryFactory = new GeometryFactory(new PrecisionModel(), 4326);
        var geometry = geometryFactory.ToGeometry(envelope);
        var wkbWriter = new WKBWriter();
        var wkb = wkbWriter.Write(geometry);

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

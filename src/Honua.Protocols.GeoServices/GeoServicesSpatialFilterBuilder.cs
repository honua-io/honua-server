// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices;

internal static class GeoServicesSpatialFilterBuilder
{
    public static SpatialFilter BuildSpatialFilter(
        QueryParameters queryParams,
        GeoServicesGeometry geometry,
        int? inputSrid)
    {
        var wkbBytes = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, inputSrid);

        if (queryParams.NearestCount.HasValue && queryParams.NearestCount.Value > 0)
        {
            return SpatialFilter.CreateKnnFilter(
                wkbBytes,
                queryParams.NearestCount.Value,
                queryParams.ReturnDistance,
                inputSrid);
        }

        var relationship = ParseSpatialRelationship(queryParams.SpatialRel);
        if (relationship is SpatialRelationship.WithinDistance or SpatialRelationship.BeyondDistance)
        {
            if (!queryParams.Distance.HasValue || queryParams.Distance.Value <= 0)
            {
                throw new ArgumentException("Distance parameter is required for distance-based spatial queries");
            }

            var (distance, unit) = NormalizeDistance(queryParams.Distance.Value, queryParams.Units);
            return SpatialFilter.CreateDistanceFilter(
                wkbBytes,
                distance,
                unit,
                relationship == SpatialRelationship.WithinDistance,
                inputSrid) with
            {
                // `returnDistance` is spec-compliant for the general /query operation: the
                // store computes the geodesic distance from the query geometry for each
                // matched feature when this flag is set, not only for KNN queries.
                ReturnDistance = queryParams.ReturnDistance
            };
        }

        // Esri semantics: a `distance` (+ optional `units`) supplied alongside the default
        // intersects relationship buffers the input geometry by that distance before the
        // spatial test, matching every feature within the buffer. Without this, the distance
        // was silently ignored and an exact intersects against a point returned nothing.
        // A `distance` paired with an explicit non-distance relationship (within/contains/...)
        // is left to that relationship's exact predicate.
        if (queryParams.Distance is > 0 && relationship == SpatialRelationship.Intersects)
        {
            var (distance, unit) = NormalizeDistance(queryParams.Distance.Value, queryParams.Units);
            return SpatialFilter.CreateDistanceFilter(
                wkbBytes,
                distance,
                unit,
                withinDistance: true,
                inputSrid) with
            {
                ReturnDistance = queryParams.ReturnDistance
            };
        }

        var isSimpleEnvelope = geometry is
        {
            Xmin: not null,
            Ymin: not null,
            Xmax: not null,
            Ymax: not null
        } && geometry.Xmin.Value <= geometry.Xmax.Value;

        return new SpatialFilter
        {
            Geometry = wkbBytes,
            SpatialRelationship = relationship,
            Srid = inputSrid,
            ReturnDistance = queryParams.ReturnDistance,
            IsSimpleEnvelope = isSimpleEnvelope,
            AllowEnvelopeOnly = relationship == SpatialRelationship.EnvelopeIntersects && isSimpleEnvelope,
            EnvelopeMinX = isSimpleEnvelope ? geometry.Xmin : null,
            EnvelopeMinY = isSimpleEnvelope ? geometry.Ymin : null,
            EnvelopeMaxX = isSimpleEnvelope ? geometry.Xmax : null,
            EnvelopeMaxY = isSimpleEnvelope ? geometry.Ymax : null
        };
    }

    private static SpatialRelationship ParseSpatialRelationship(string? spatialRel)
    {
        return spatialRel?.ToLowerInvariant() switch
        {
            "esrispatialrelintersects" or null => SpatialRelationship.Intersects,
            "esrispatialrelcontains" => SpatialRelationship.Contains,
            "esrispatialrelwithin" => SpatialRelationship.Within,
            "esrispatialrelenvelopeintersects" => SpatialRelationship.EnvelopeIntersects,
            "esrispatialrelcrosses" => SpatialRelationship.Crosses,
            "esrispatialreltouches" => SpatialRelationship.Touches,
            "esrispatialreloverlaps" => SpatialRelationship.Overlaps,
            "esrispatialreldisjoint" => SpatialRelationship.Disjoint,
            "esrispatialrelequals" => SpatialRelationship.Equals,
            "esrispatialrelwithindistance" => SpatialRelationship.WithinDistance,
            "esrispatialrelbeyonddistance" => SpatialRelationship.BeyondDistance,
            _ => throw new ArgumentException($"Unsupported spatial relationship: {spatialRel}")
        };
    }

    /// <summary>
    /// Esri <c>esriSRUnit_NauticalMile</c>: the international nautical mile, exactly 1852 m.
    /// </summary>
    private const double MetersPerNauticalMile = 1852.0;

    /// <summary>
    /// Esri <c>esriSRUnit_USNauticalMile</c>: the US nautical mile, 1853.248 m.
    /// </summary>
    private const double MetersPerUsNauticalMile = 1853.248;

    /// <summary>
    /// Normalizes the Esri <c>units</c> token and distance into the canonical
    /// <see cref="DistanceUnit"/> the store understands. Nautical-mile units have no
    /// canonical enum member, so they are converted to meters here at the protocol
    /// adapter. Unrecognized unit tokens are rejected (a silent meters fallback would
    /// return wrong answers — e.g. a nautical-mile radius shrunk 1852x).
    /// </summary>
    private static (double Distance, DistanceUnit Unit) NormalizeDistance(double distance, string? units)
    {
        return units?.ToLowerInvariant() switch
        {
            "esrisrunit_meter" or null => (distance, DistanceUnit.Meters),
            "esrisrunit_foot" => (distance, DistanceUnit.Feet),
            "esrisrunit_kilometer" => (distance, DistanceUnit.Kilometers),
            "esrisrunit_statutemile" => (distance, DistanceUnit.Miles),
            "esrisrunit_nauticalmile" => (distance * MetersPerNauticalMile, DistanceUnit.Meters),
            "esrisrunit_usnauticalmile" => (distance * MetersPerUsNauticalMile, DistanceUnit.Meters),
            "meters" or "m" => (distance, DistanceUnit.Meters),
            "feet" or "ft" => (distance, DistanceUnit.Feet),
            "kilometers" or "km" => (distance, DistanceUnit.Kilometers),
            "miles" or "mi" => (distance, DistanceUnit.Miles),
            "nauticalmiles" or "nm" => (distance * MetersPerNauticalMile, DistanceUnit.Meters),
            _ => throw new ArgumentException($"Unsupported distance units: {units}")
        };
    }
}

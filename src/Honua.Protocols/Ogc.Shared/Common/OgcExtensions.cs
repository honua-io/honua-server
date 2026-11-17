// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Linq;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Protocols.Ogc.Common;

public static class OgcExtensions
{
    public static FeatureExtent ToFeatureExtent(this SpatialExtent spatialExtent)
    {
        if (spatialExtent.BoundingBox.Length == 0 || spatialExtent.BoundingBox[0].Length != 4)
            throw new ArgumentException("SpatialExtent must have a valid bounding box");

        var bbox = spatialExtent.BoundingBox[0];
        if (!ExtentExtensions.TryExtractSridFromCrs(spatialExtent.Crs, out var srid))
        {
            throw new ArgumentException("SpatialExtent CRS must be a supported EPSG or CRS84 identifier.", nameof(spatialExtent));
        }

        return FeatureExtent.Create(bbox[0], bbox[1], bbox[2], bbox[3], srid);
    }

    public static GeoJsonFeature ToOgcGeoJsonFeature(this GeoJsonFeatureBase featureBase,
        SimpleGeoJsonGeometry? geometry = null,
        ImmutableArray<Link>? links = null)
        => CreateOgcGeoJsonFeature(featureBase, featureBase.Properties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), geometry, links);

    // Only the internal response builder may transfer its newly created dictionary.
    // Public conversion retains its detached-copy contract for arbitrary callers.
    internal static GeoJsonFeature ToOgcGeoJsonFeatureWithOwnedProperties(
        this GeoJsonFeatureBase featureBase,
        Dictionary<string, object?> properties,
        SimpleGeoJsonGeometry? geometry,
        ImmutableArray<Link>? links)
        => CreateOgcGeoJsonFeature(featureBase, properties, geometry, links);

    private static GeoJsonFeature CreateOgcGeoJsonFeature(
        GeoJsonFeatureBase featureBase,
        Dictionary<string, object?> properties,
        SimpleGeoJsonGeometry? geometry,
        ImmutableArray<Link>? links)
    {
        // Per RFC 7946, feature id can be a number or string.
        // Preserve the original type for round-trip fidelity.
        object? id = featureBase.Id switch
        {
            long longId => longId,
            int intId => (long)intId,
            string strId => strId,
            _ => null
        };

        return new()
        {
            Type = "Feature",
            Id = id,
            Properties = properties,
            Geometry = geometry,
            Links = links
        };
    }

    public static GeoJsonFeatureBase ToGeoJsonFeatureBase(this GeoJsonFeature ogcFeature)
    {
        // Convert object? id back to the shared model's expected type
        object? id = ogcFeature.Id switch
        {
            long l => l,
            int i => (long)i,
            string s => s,
            _ => ogcFeature.Id
        };

        return GeoJsonFeatureBase.Create(
            id,
            ogcFeature.Properties.AsReadOnly(),
            ogcFeature.Geometry != null);
    }

    public static PagedResponseBase ToPagedResponseBase(long? numberMatched, int numberReturned)
        => PagedResponseBase.Create(numberReturned, numberMatched);
}

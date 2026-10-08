// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.GeoJson;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcGeoJsonFeatureBuilder
{
    internal static GeoJsonFeature Create(
        Feature feature,
        MetadataV2Resource resource,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        IReadOnlySet<string>? projectedProperties = null,
        Func<long, object?>? idFactory = null,
        ImmutableArray<Link>? links = null,
        GeoJsonFeatureBaseBuilder.PreparedSchema? schema = null)
    {
        var geometry = geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return CreateCore(
            GeoJsonFeatureBaseBuilder.Create(
                feature,
                resource,
                new GeoJsonFeatureBuildOptions(
                    ProjectedProperties: projectedProperties,
                    IncludeObjectIdProperty: ShouldIncludePublicIdentifierProperty(resource, schema),
                    IdFactory: idFactory ?? (_ => OgcFeatureIdentifierResolver.GetPublicId(feature, resource)),
                    Schema: schema)),
            geometry,
            links, ownsProperties: true);
    }

    internal static GeoJsonFeature Create(
        EncodedGeoJsonFeature feature,
        MetadataV2Resource resource,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        IReadOnlySet<string>? projectedProperties = null,
        Func<long, object?>? idFactory = null,
        ImmutableArray<Link>? links = null,
        GeoJsonFeatureBaseBuilder.PreparedSchema? schema = null)
    {
        var geometry = geometryServices.ConvertGeoJsonToSimpleGeometry(feature.GeometryGeoJson, axisOrder);
        return CreateCore(
            GeoJsonFeatureBaseBuilder.Create(
                feature,
                resource,
                new GeoJsonFeatureBuildOptions(
                    ProjectedProperties: projectedProperties,
                    IncludeObjectIdProperty: ShouldIncludePublicIdentifierProperty(resource, schema),
                    IdFactory: idFactory ?? (_ => OgcFeatureIdentifierResolver.GetPublicId(feature, resource)),
                    Schema: schema)),
            geometry,
            links, ownsProperties: true);
    }

    private static bool ShouldIncludePublicIdentifierProperty(
        MetadataV2Resource resource, GeoJsonFeatureBaseBuilder.PreparedSchema? schema)
    {
        var name = schema?.ObjectIdFieldName ?? resource.FindPrimaryIdField()?.Name ?? "objectid";
        return !name.Equals(FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase);
    }

    internal static FeatureCollection CreateCollection(
        IReadOnlyCollection<GeoJsonFeature> features,
        long? numberMatched,
        ImmutableArray<Link>? links = null)
        => new()
        {
            Features = [.. features],
            NumberMatched = numberMatched,
            NumberReturned = features.Count,
            Links = links,
            TimeStamp = DateTimeOffset.UtcNow
        };

    // Caller transfers the exact array it has materialized for this response.
    internal static FeatureCollection CreateCollectionWithOwnedFeatures(
        GeoJsonFeature[] features,
        long? numberMatched,
        ImmutableArray<Link>? links = null)
        => new()
        {
            Features = features,
            NumberMatched = numberMatched,
            NumberReturned = features.Length,
            Links = links,
            TimeStamp = DateTimeOffset.UtcNow
        };

    internal static GeoJsonFeature Create(
        object? id,
        IReadOnlyDictionary<string, object?> properties,
        SimpleGeoJsonGeometry? geometry = null,
        ImmutableArray<Link>? links = null)
        => CreateCore(
            GeoJsonFeatureBase.Create(id, properties, geometry is not null),
            geometry,
            links);

    private static GeoJsonFeature CreateCore(
        GeoJsonFeatureBase featureBase,
        SimpleGeoJsonGeometry? geometry,
        ImmutableArray<Link>? links,
        bool ownsProperties = false)
        => ownsProperties && featureBase.Properties is Dictionary<string, object?> properties
            ? featureBase.ToOgcGeoJsonFeatureWithOwnedProperties(properties, geometry, links)
            : featureBase.ToOgcGeoJsonFeature(geometry, links);
}

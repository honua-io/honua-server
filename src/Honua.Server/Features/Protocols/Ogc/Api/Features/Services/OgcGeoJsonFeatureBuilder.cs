// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.GeoJson;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features.Services;

internal static class OgcGeoJsonFeatureBuilder
{
    internal static GeoJsonFeature Create(
        Feature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        IReadOnlySet<string>? projectedProperties = null,
        Func<long, object?>? idFactory = null,
        ImmutableArray<Link>? links = null)
    {
        var geometry = geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return CreateCore(
            GeoJsonFeatureBaseBuilder.Create(
                feature,
                layer,
                new GeoJsonFeatureBuildOptions(
                    ProjectedProperties: projectedProperties,
                    IdFactory: idFactory ?? (_ => OgcFeatureIdentifierResolver.GetPublicId(feature, layer)))),
            geometry,
            links);
    }

    internal static GeoJsonFeature Create(
        EncodedGeoJsonFeature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        OgcFeaturesGeometryServices geometryServices,
        IReadOnlySet<string>? projectedProperties = null,
        Func<long, object?>? idFactory = null,
        ImmutableArray<Link>? links = null)
    {
        var geometry = geometryServices.ConvertGeoJsonToSimpleGeometry(feature.GeometryGeoJson, axisOrder);
        return CreateCore(
            GeoJsonFeatureBaseBuilder.Create(
                feature,
                layer,
                new GeoJsonFeatureBuildOptions(
                    ProjectedProperties: projectedProperties,
                    IdFactory: idFactory ?? (_ => OgcFeatureIdentifierResolver.GetPublicId(feature, layer)))),
            geometry,
            links);
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
        ImmutableArray<Link>? links)
        => featureBase.ToOgcGeoJsonFeature(geometry, links);
}

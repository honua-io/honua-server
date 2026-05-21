// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Read-only spatial-analytics surface (Esri FeatureServer mirror and OGC API
// Features mirror); anonymous by design. Every POST takes a JSON body that
// specifies the filter/clustering/buffer parameters and returns derived
// analytic features — no server-side mutation. Each route opts into
// AllowAnonymous explicitly so authorization-policy tooling can see the
// intent rather than treating it as an accidental gap; per-layer access
// policies still gate the underlying data via the shared handler stack.

namespace Honua.Server.Features.Protocols.SpatialAnalytics;

/// <summary>
/// Maps the Pro-tier spatial analytics endpoints onto the REST FeatureServer
/// and OGC API Features route families. Both families delegate to the same
/// handler implementations in <see cref="SpatialAnalyticsRequestHandlers"/> so
/// clients get identical semantics (and identical payloads) regardless of
/// whether they call the Esri-style or OGC-style route.
/// </summary>
internal static class SpatialAnalyticsEndpoints
{
    /// <summary>
    /// Registers the four spatial analytics REST endpoints under the
    /// <c>/rest/services/{serviceId}/FeatureServer/{layerId}</c> prefix.
    /// </summary>
    public static IEndpointRouteBuilder MapSpatialAnalyticsRestEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
            "/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryClusters",
            SpatialAnalyticsRequestHandlers.HandleQueryClustersPost)
            .WithDisplayName("Query Clusters (POST)")
            .WithName("QueryClustersPost")
            .WithSummary("Cluster features with DBSCAN or K-Means")
            .WithDescription(
                "Runs a server-side clustering pass (DBSCAN or K-Means) over the filtered "
                + "subset of a layer and returns either one row per feature carrying a clusterId "
                + "or one row per cluster with hull geometry and aggregate statistics.")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .AllowAnonymous();

        endpoints.MapPost(
            "/rest/services/{serviceId}/FeatureServer/{layerId:int}/spatialJoin",
            SpatialAnalyticsRequestHandlers.HandleSpatialJoinPost)
            .WithDisplayName("Spatial Join (POST)")
            .WithName("SpatialJoinPost")
            .WithSummary("Enrich target features with attributes from a join layer")
            .WithDescription(
                "Joins a target layer to a join layer using a spatial predicate "
                + "(intersects, contains, within, dwithin) and returns each target feature "
                + "with a match count, optional carried attributes and aggregate statistics "
                + "over the matching join rows.")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .AllowAnonymous();

        endpoints.MapPost(
            "/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryBufferAggregate",
            SpatialAnalyticsRequestHandlers.HandleBufferAggregatePost)
            .WithDisplayName("Buffer Aggregate (POST)")
            .WithName("BufferAggregatePost")
            .WithSummary("Buffer features and dissolve the result")
            .WithDescription(
                "Buffers each input feature by a fixed distance and either dissolves the "
                + "result with ST_Union per optional group or returns the individual buffers.")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .AllowAnonymous();

        endpoints.MapPost(
            "/rest/services/{serviceId}/FeatureServer/{layerId:int}/queryDensity",
            SpatialAnalyticsRequestHandlers.HandleDensityPost)
            .WithDisplayName("Query Density (POST)")
            .WithName("QueryDensityPost")
            .WithSummary("Bin features into a hex or square grid")
            .WithDescription(
                "Bins the filtered subset of a layer into a hex or square grid in Web Mercator "
                + "and returns one row per occupied cell with the cell geometry and a count or "
                + "weighted sum.")
            .WithTags("FeatureServer")
            .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Post }))
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Registers the four spatial analytics OGC API Features mirror endpoints
    /// under the <c>/ogc/features/collections/{collectionId}</c> prefix.
    /// </summary>
    public static IEndpointRouteBuilder MapSpatialAnalyticsOgcEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(
            "/ogc/features/collections/{collectionId}/clusters",
            SpatialAnalyticsRequestHandlers.HandleOgcClustersPost)
            .WithDisplayName("OGC Features Clusters")
            .WithName("OgcFeaturesClusters")
            .WithSummary("Cluster collection features with DBSCAN or K-Means")
            .WithDescription(
                "OGC API Features mirror of queryClusters. Returns a GeoJSON FeatureCollection "
                + "whose features either represent individual source rows with assigned cluster "
                + "ids or one feature per cluster carrying the convex hull geometry.")
            .WithTags("OGC API Features")
            .AllowAnonymous();

        endpoints.MapPost(
            "/ogc/features/collections/{collectionId}/spatial-join",
            SpatialAnalyticsRequestHandlers.HandleOgcSpatialJoinPost)
            .WithDisplayName("OGC Features Spatial Join")
            .WithName("OgcFeaturesSpatialJoin")
            .WithSummary("Enrich collection features with attributes from a join layer")
            .WithDescription(
                "OGC API Features mirror of spatialJoin. Returns a GeoJSON FeatureCollection "
                + "of target features enriched with match counts and optional carry attributes.")
            .WithTags("OGC API Features")
            .AllowAnonymous();

        endpoints.MapPost(
            "/ogc/features/collections/{collectionId}/buffer-aggregate",
            SpatialAnalyticsRequestHandlers.HandleOgcBufferAggregatePost)
            .WithDisplayName("OGC Features Buffer Aggregate")
            .WithName("OgcFeaturesBufferAggregate")
            .WithSummary("Buffer collection features and dissolve the result")
            .WithDescription(
                "OGC API Features mirror of queryBufferAggregate. Returns a GeoJSON "
                + "FeatureCollection with dissolved buffer polygons per group.")
            .WithTags("OGC API Features")
            .AllowAnonymous();

        endpoints.MapPost(
            "/ogc/features/collections/{collectionId}/density",
            SpatialAnalyticsRequestHandlers.HandleOgcDensityPost)
            .WithDisplayName("OGC Features Density")
            .WithName("OgcFeaturesDensity")
            .WithSummary("Bin collection features into a hex or square grid")
            .WithDescription(
                "OGC API Features mirror of queryDensity. Returns a GeoJSON FeatureCollection "
                + "of density cells with counts or weighted sums.")
            .WithTags("OGC API Features")
            .AllowAnonymous();

        return endpoints;
    }
}

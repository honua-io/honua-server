// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Extension methods to register OGC API Features endpoints
/// </summary>
public static class OgcFeaturesEndpoints
{
    /// <summary>
    /// Maps OGC API Features endpoints for Core and Conformance
    /// </summary>
    public static IEndpointRouteBuilder MapOgcFeaturesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features", HandleGetLandingPage)
            .WithDisplayName("OGC API Features Landing Page")
            .WithName("OgcLandingPage")
            .WithSummary("Get OGC API Features landing page")
            .WithDescription("The landing page provides links to the API definition and other resources")
            .WithTags("OGC API Features")
            .CacheOutput("OgcLandingPage")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections", HandleGetCollections)
            .WithDisplayName("OGC API Features Collections")
            .WithName("CollectionInfos")
            .WithSummary("Get OGC API Features collections")
            .WithDescription("Lists all available feature collections")
            .WithTags("OGC API Features")
            .CacheOutput("CollectionInfos")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Features Collection")
            .WithName("CollectionInfo")
            .WithSummary("Get OGC API Features collection metadata")
            .WithDescription("Get detailed metadata for a specific collection")
            .WithTags("OGC API Features")
            .CacheOutput("CollectionInfo")
            .Produces<CollectionInfo>(200, MediaTypes.Json)
            .Produces(404);

        return endpoints;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static Ok<LandingPage> HandleGetLandingPage(HttpContext context)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Links = ImmutableArray.Create(
                // Self link
                Link.Create(
                    href: $"{baseUrl}/ogc/features",
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: "This document"
                ),

                // API definition
                Link.Create(
                    href: $"{baseUrl}/openapi.json",
                    rel: RelationTypes.ServiceDesc,
                    type: MediaTypes.OpenApi,
                    title: "API definition"
                ),

                // Conformance declaration
                Link.Create(
                    href: $"{baseUrl}/ogc/features/conformance",
                    rel: RelationTypes.Conformance,
                    type: MediaTypes.Json,
                    title: "Conformance declaration"
                ),

                // Collections (will be implemented in issue #16)
                Link.Create(
                    href: $"{baseUrl}/ogc/features/collections",
                    rel: RelationTypes.Data,
                    type: MediaTypes.Json,
                    title: "Feature collections"
                )
            )
        };

        return TypedResults.Ok(landingPage);
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static Ok<ConformanceDeclaration> HandleGetConformance()
    {
        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                // OGC API Features Core
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/oas30",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-features-1/1.0/conf/geojson",

                // OGC API Common
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/landing-page",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            )
        };

        return TypedResults.Ok(conformance);
    }

    /// <summary>
    /// Handles the OGC API Features collections list request
    /// </summary>
    private static async Task<Results<Ok<Collections>, NotFound>> HandleGetCollections(
        HttpContext context, ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var layers = await layerCatalog.ListLayersAsync();
            var collections = layers.Select(layer => CreateCollection(layer, baseUrl)).ToImmutableArray();

            var response = new Collections
            {
                CollectionList = collections,
                Links = ImmutableArray.Create(
                    // Self link
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections",
                        rel: RelationTypes.Self,
                        type: MediaTypes.Json,
                        title: "Collections"
                    ),

                    // Parent (landing page)
                    Link.Create(
                        href: $"{baseUrl}/ogc/features",
                        rel: "parent",
                        type: MediaTypes.Json,
                        title: "Landing page"
                    )
                )
            };

            return TypedResults.Ok(response);
        }
        catch
        {
            return TypedResults.NotFound();
        }
    }

    /// <summary>
    /// Handles the OGC API Features single collection request
    /// </summary>
    private static async Task<Results<Ok<CollectionInfo>, NotFound>> HandleGetCollection(
        string collectionId, HttpContext context, ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            // OGC collection IDs are strings, but layer catalog uses int IDs
            // For now, try to parse the string as an integer
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            var layer = await layerCatalog.GetLayerAsync(layerId);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            var collection = CreateCollection(layer, baseUrl);
            return TypedResults.Ok(collection);
        }
        catch
        {
            return TypedResults.NotFound();
        }
    }

    /// <summary>
    /// Converts a layer definition to OGC API Features collection
    /// </summary>
    private static CollectionInfo CreateCollection(
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer, string baseUrl)
    {
        // Use layer ID as collection ID (string representation)
        var collectionId = layer.Id.ToString();
        var collectionLinks = ImmutableArray.Create(
            // Self link
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: layer.Name
            ),

            // Items link (for issue #17)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Data,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),

            // Parent (collections)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections",
                rel: "parent",
                type: MediaTypes.Json,
                title: "Collections"
            )
        );

        // Create spatial extent if geometry information is available
        SpatialExtent? spatialExtent = null;
        if (layer.HasGeometry)
        {
            // Default extent for now - in a real implementation this would come from the actual data
            spatialExtent = new SpatialExtent
            {
                BoundingBox = ImmutableArray.Create(
                    ImmutableArray.Create(-180.0, -90.0, 180.0, 90.0)
                )
            };
        }

        var extent = spatialExtent != null ? new Extent { Spatial = spatialExtent } : null;

        return new CollectionInfo
        {
            Id = collectionId,
            Title = layer.Name,
            Description = layer.Description,
            Links = collectionLinks,
            Extent = extent,
            ItemType = "feature",
            Crs = ImmutableArray.Create(
                "http://www.opengis.net/def/crs/OGC/1.3/CRS84",
                "http://www.opengis.net/def/crs/EPSG/0/4326"
            )
        };
    }
}

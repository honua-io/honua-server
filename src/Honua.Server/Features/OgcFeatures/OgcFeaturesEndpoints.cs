// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
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

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Features Items")
            .WithName("GetItems")
            .WithSummary("Get features from a collection")
            .WithDescription("Get features from a collection with optional filtering using CQL2-Text")
            .WithTags("OGC API Features")
            .Produces<FeatureCollection>(200, MediaTypes.GeoJson)
            .Produces(400)
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
    /// Handles the OGC API Features items request with optional CQL filtering
    /// </summary>
    private static async Task<IResult> HandleGetItems(
        string collectionId,
        string? filter,
        int? limit,
        int? offset,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.FeatureServer.Services.IGeometryConverter geometryConverter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validate limit and offset parameters
            if (limit.HasValue && limit.Value <= 0)
            {
                return TypedResults.BadRequest("Limit must be a positive integer");
            }

            if (offset.HasValue && offset.Value < 0)
            {
                return TypedResults.BadRequest("Offset must be non-negative");
            }

            // Apply reasonable maximum limit to prevent unbounded queries
            const int maxLimit = 10000;
            if (limit.HasValue && limit.Value > maxLimit)
            {
                return TypedResults.BadRequest($"Limit cannot exceed {maxLimit}");
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Verify collection/layer exists
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Parse CQL filter if provided
            FilterExpression? filterExpression = null;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                try
                {
                    var parser = new Cql2Parser();
                    filterExpression = parser.Parse(filter);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest($"Invalid CQL filter: {ex.Message}");
                }
            }

            var whereClause = string.IsNullOrWhiteSpace(filter) ? null : filter;

            // Create feature query with proper parameterized filter support
            FeatureQuery featureQuery;
            if (filterExpression != null)
            {
                var translator = new SqlFilterTranslator();
                var sqlFragment = translator.Translate(filterExpression, layer);

                // Use parameterized SQL filter to preserve parameters
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SqlFilter = sqlFragment,
                    Limit = limit ?? 1000, // Default limit per OGC spec
                    Offset = offset
                };
            }
            else
            {
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    Limit = limit ?? 1000, // Default limit per OGC spec
                    Offset = offset
                };
            }

            // Query features
            var result = await featureStore.QueryAsync(layerId, featureQuery, cancellationToken);

            // Convert to GeoJSON FeatureCollection
            var featureCollection = ConvertToGeoJsonFeatureCollection(result, layer, geometryConverter);

            return Results.Json(featureCollection, contentType: MediaTypes.GeoJson);
        }
        catch (ArgumentException ex)
        {
            // Client errors - invalid parameters, filters, etc.
            return TypedResults.BadRequest($"Invalid request: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // Client errors - invalid operations like layer not found
            return TypedResults.BadRequest($"Invalid operation: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Server errors - log details but don't expose to client
            // TODO: Add proper logging here (ex: logger.LogError(ex, "Error processing OGC features request"))
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while processing the request.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Converts query results to GeoJSON FeatureCollection
    /// </summary>
    private static FeatureCollection ConvertToGeoJsonFeatureCollection(
        QueryResult<Feature> queryResult,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        Honua.Server.Features.FeatureServer.Services.IGeometryConverter geometryConverter)
    {
        var features = queryResult.Items.Select(feature => new
        {
            type = "Feature",
            id = feature.Id,
            geometry = ConvertFeatureGeometry(feature.Geometry, geometryConverter),
            properties = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToArray();

        return new FeatureCollection
        {
            Type = "FeatureCollection",
            Features = features,
            NumberMatched = queryResult.TotalCount,
            NumberReturned = queryResult.Items.Length
        };
    }

    /// <summary>
    /// Converts a feature's WKB geometry to GeoJSON format
    /// </summary>
    private static object? ConvertFeatureGeometry(byte[]? wkbGeometry, Honua.Server.Features.FeatureServer.Services.IGeometryConverter geometryConverter)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
            return null;

        try
        {
            return geometryConverter.ConvertWkbToGeoJson(wkbGeometry);
        }
        catch (ArgumentException)
        {
            // If geometry conversion fails, return null rather than throwing
            // This allows the feature to be returned without geometry
            return null;
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

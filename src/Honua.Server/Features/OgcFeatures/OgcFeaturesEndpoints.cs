// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
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

        endpoints.MapGet("/openapi.json", HandleGetOpenApiSpec)
            .WithDisplayName("OGC API Features OpenAPI Specification")
            .WithName("OpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for OGC API Features")
            .WithDescription("The OpenAPI specification describes all available endpoints and their parameters")
            .WithTags("OGC API Features")
            .CacheOutput("OgcOpenApi")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections", HandleGetCollections)
            .WithDisplayName("OGC API Features Collections")
            .WithName("CollectionInfos")
            .WithSummary("Get OGC API Features collections")
            .WithDescription("Lists all available feature collections")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollections")
            .Produces<Collections>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Features Collection")
            .WithName("CollectionInfo")
            .WithSummary("Get OGC API Features collection metadata")
            .WithDescription("Get detailed metadata for a specific collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollection")
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

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items/{featureId}", HandleGetItem)
            .WithDisplayName("OGC API Features Item")
            .WithName("GetItem")
            .WithSummary("Get a specific feature from a collection")
            .WithDescription("Get a specific feature by its ID from a collection")
            .WithTags("OGC API Features")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(404)
            .Produces(400);

        return endpoints;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static IResult HandleGetLandingPage(HttpContext context, string? f = null)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        
        // Determine output format
        var (outputFormat, contentType) = DetermineOutputFormat(context, f);

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

                // Collections
                Link.Create(
                    href: $"{baseUrl}/ogc/features/collections",
                    rel: RelationTypes.Data,
                    type: MediaTypes.Json,
                    title: "Feature collections"
                )
            )
        };

        // Return format-specific response
        return outputFormat switch
        {
            "html" => TypedResults.BadRequest("HTML format is not yet implemented"),
            "json" or "geojson" => Results.Json(landingPage, OgcJsonContext.Default.LandingPage, contentType: contentType),
            _ => Results.Json(landingPage, OgcJsonContext.Default.LandingPage, contentType: MediaTypes.Json)
        };
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static IResult HandleGetConformance(HttpContext context, string? f = null)
    {
        // Determine output format
        var (outputFormat, contentType) = DetermineOutputFormat(context, f);

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

        // Return format-specific response
        return outputFormat switch
        {
            "html" => TypedResults.BadRequest("HTML format is not yet implemented"),
            "json" or "geojson" => Results.Json(conformance, OgcJsonContext.Default.ConformanceDeclaration, contentType: contentType),
            _ => Results.Json(conformance, OgcJsonContext.Default.ConformanceDeclaration, contentType: MediaTypes.Json)
        };
    }

    /// <summary>
    /// Handles the OpenAPI 3.0 specification request
    /// </summary>
    private static Ok<object> HandleGetOpenApiSpec(HttpContext context)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        var openApiSpec = new
        {
            openapi = "3.0.3",
            info = new
            {
                title = "Honua OGC API Features",
                description = "OGC API Features implementation for geospatial data access",
                version = "1.0.0",
                contact = new
                {
                    name = "Honua Server",
                    url = $"{baseUrl}"
                }
            },
            servers = new[]
            {
                new { url = baseUrl, description = "Honua Server" }
            },
            paths = new
            {
                @"/ogc/features" = new
                {
                    get = new
                    {
                        summary = "Get OGC API Features landing page",
                        description = "The landing page provides links to the API definition and other resources",
                        tags = new[] { "OGC API Features" },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "Landing page information",
                                content = new
                                {
                                    @"application/json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/LandingPage" }
                                    }
                                }
                            }
                        }
                    }
                },
                @"/ogc/features/conformance" = new
                {
                    get = new
                    {
                        summary = "Get OGC API Features conformance declaration",
                        description = "Conformance classes that this API conforms to",
                        tags = new[] { "OGC API Features" },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "Conformance declaration",
                                content = new
                                {
                                    @"application/json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/ConformanceDeclaration" }
                                    }
                                }
                            }
                        }
                    }
                },
                @"/ogc/features/collections" = new
                {
                    get = new
                    {
                        summary = "Get OGC API Features collections",
                        description = "Lists all available feature collections",
                        tags = new[] { "OGC API Features" },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "Collections list",
                                content = new
                                {
                                    @"application/json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/Collections" }
                                    }
                                }
                            }
                        }
                    }
                },
                @"/ogc/features/collections/{collectionId}" = new
                {
                    get = new
                    {
                        summary = "Get OGC API Features collection metadata",
                        description = "Get detailed metadata for a specific collection",
                        tags = new[] { "OGC API Features" },
                        parameters = new[]
                        {
                            new
                            {
                                name = "collectionId",
                                @in = "path",
                                required = true,
                                description = "Collection identifier",
                                schema = new { type = "string" }
                            }
                        },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "Collection metadata",
                                content = new
                                {
                                    @"application/json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/CollectionInfo" }
                                    }
                                }
                            },
                            @"404" = new
                            {
                                description = "Collection not found"
                            }
                        }
                    }
                },
                @"/ogc/features/collections/{collectionId}/items" = new
                {
                    get = new
                    {
                        summary = "Get features from a collection",
                        description = "Get features from a collection with optional filtering",
                        tags = new[] { "OGC API Features" },
                        parameters = new[]
                        {
                            new
                            {
                                name = "collectionId",
                                @in = "path",
                                required = true,
                                description = "Collection identifier",
                                schema = new { type = "string" }
                            },
                            new
                            {
                                name = "limit",
                                @in = "query",
                                required = false,
                                description = "Maximum number of features to return (default: 10, max: 10000)",
                                schema = new { type = "integer", minimum = 1, maximum = 10000, @default = 10 }
                            },
                            new
                            {
                                name = "offset",
                                @in = "query",
                                required = false,
                                description = "Number of features to skip",
                                schema = new { type = "integer", minimum = 0, @default = 0 }
                            },
                            new
                            {
                                name = "bbox",
                                @in = "query",
                                required = false,
                                description = "Bounding box as 'minx,miny,maxx,maxy'",
                                schema = new { type = "string", pattern = @"^-?\d+\.?\d*,-?\d+\.?\d*,-?\d+\.?\d*,-?\d+\.?\d*$" }
                            },
                            new
                            {
                                name = "datetime",
                                @in = "query",
                                required = false,
                                description = "Temporal filter (ISO 8601 format) - not yet implemented",
                                schema = new { type = "string" }
                            },
                            new
                            {
                                name = "filter",
                                @in = "query",
                                required = false,
                                description = "CQL2-Text filter expression",
                                schema = new { type = "string" }
                            }
                        },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "GeoJSON FeatureCollection",
                                content = new
                                {
                                    @"application/geo+json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/FeatureCollection" }
                                    }
                                }
                            },
                            @"400" = new
                            {
                                description = "Bad request (invalid parameters)"
                            },
                            @"404" = new
                            {
                                description = "Collection not found"
                            }
                        }
                    }
                },
                @"/ogc/features/collections/{collectionId}/items/{featureId}" = new
                {
                    get = new
                    {
                        summary = "Get a specific feature from a collection",
                        description = "Get a specific feature by its ID from a collection",
                        tags = new[] { "OGC API Features" },
                        parameters = new[]
                        {
                            new
                            {
                                name = "collectionId",
                                @in = "path",
                                required = true,
                                description = "Collection identifier",
                                schema = new { type = "string" }
                            },
                            new
                            {
                                name = "featureId",
                                @in = "path",
                                required = true,
                                description = "Feature identifier",
                                schema = new { type = "string" }
                            }
                        },
                        responses = new
                        {
                            @"200" = new
                            {
                                description = "GeoJSON Feature",
                                content = new
                                {
                                    @"application/geo+json" = new
                                    {
                                        schema = new { @"$ref" = "#/components/schemas/GeoJsonFeature" }
                                    }
                                }
                            },
                            @"404" = new
                            {
                                description = "Feature or collection not found"
                            }
                        }
                    }
                }
            },
            components = new
            {
                schemas = new
                {
                    LandingPage = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string" },
                            description = new { type = "string" },
                            links = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/Link" }
                            }
                        }
                    },
                    ConformanceDeclaration = new
                    {
                        type = "object",
                        properties = new
                        {
                            conformsTo = new
                            {
                                type = "array",
                                items = new { type = "string" }
                            }
                        }
                    },
                    Collections = new
                    {
                        type = "object",
                        properties = new
                        {
                            collections = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/CollectionInfo" }
                            },
                            links = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/Link" }
                            }
                        }
                    },
                    CollectionInfo = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string" },
                            title = new { type = "string" },
                            description = new { type = "string" },
                            extent = new { @"$ref" = "#/components/schemas/Extent" },
                            links = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/Link" }
                            }
                        }
                    },
                    FeatureCollection = new
                    {
                        type = "object",
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "FeatureCollection" } },
                            features = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/GeoJsonFeature" }
                            },
                            numberMatched = new { type = "integer" },
                            numberReturned = new { type = "integer" },
                            timeStamp = new { type = "string", format = "date-time" },
                            links = new
                            {
                                type = "array",
                                items = new { @"$ref" = "#/components/schemas/Link" }
                            }
                        }
                    },
                    GeoJsonFeature = new
                    {
                        type = "object",
                        properties = new
                        {
                            type = new { type = "string", @enum = new[] { "Feature" } },
                            id = new { },
                            geometry = new { },
                            properties = new { type = "object" }
                        }
                    },
                    Link = new
                    {
                        type = "object",
                        properties = new
                        {
                            href = new { type = "string", format = "uri" },
                            rel = new { type = "string" },
                            type = new { type = "string" },
                            title = new { type = "string" }
                        },
                        required = new[] { "href", "rel" }
                    },
                    Extent = new
                    {
                        type = "object",
                        properties = new
                        {
                            spatial = new
                            {
                                type = "object",
                                properties = new
                                {
                                    bbox = new
                                    {
                                        type = "array",
                                        items = new
                                        {
                                            type = "array",
                                            items = new { type = "number" }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        return TypedResults.Ok<object>(openApiSpec);
    }

    /// <summary>
    /// Handles the OGC API Features collections list request
    /// </summary>
    private static async Task<IResult> HandleGetCollections(
        HttpContext context, ILayerCatalog layerCatalog, string? f = null)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        
        // Determine output format
        var (outputFormat, contentType) = DetermineOutputFormat(context, f);

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

            // Return format-specific response
            return outputFormat switch
            {
                "html" => TypedResults.BadRequest("HTML format is not yet implemented"),
                "json" or "geojson" => Results.Json(response, OgcJsonContext.Default.Collections, contentType: contentType),
                _ => Results.Json(response, OgcJsonContext.Default.Collections, contentType: MediaTypes.Json)
            };
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest($"Invalid request parameters: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest($"Invalid operation: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Log the actual error for debugging while not exposing internal details
            // TODO: Add proper logging here (ex: logger.LogError(ex, "Error retrieving collections"))
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while retrieving collections.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features single collection request
    /// </summary>
    private static async Task<IResult> HandleGetCollection(
        string collectionId, HttpContext context, ILayerCatalog layerCatalog, string? f = null)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        
        // Determine output format
        var (outputFormat, contentType) = DetermineOutputFormat(context, f);

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
            
            // Return format-specific response
            return outputFormat switch
            {
                "html" => TypedResults.BadRequest("HTML format is not yet implemented"),
                "json" or "geojson" => Results.Json(collection, OgcJsonContext.Default.CollectionInfo, contentType: contentType),
                _ => Results.Json(collection, OgcJsonContext.Default.CollectionInfo, contentType: MediaTypes.Json)
            };
        }
        catch (ArgumentException ex) when (ex.Message.Contains("parse") || ex.Message.Contains("invalid"))
        {
            return TypedResults.BadRequest($"Invalid collection ID: {ex.Message}");
        }
        catch (InvalidOperationException)
        {
            // Layer not found is a legitimate 404 case
            return TypedResults.NotFound();
        }
        catch (Exception ex)
        {
            // Log the actual error for debugging while not exposing internal details
            // TODO: Add proper logging here (ex: logger.LogError(ex, "Error retrieving collection {CollectionId}", collectionId))
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while retrieving the collection.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features items request with optional CQL filtering
    /// </summary>
    private static async Task<IResult> HandleGetItems(
        string collectionId,
        string? filter,
        string? bbox,
        string? datetime,
        string? f,
        int? limit,
        int? offset,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Determine output format through content negotiation
            var (outputFormat, contentType) = DetermineOutputFormat(context, f);
            
            // Validate format support
            if (outputFormat == "html")
            {
                return TypedResults.BadRequest("HTML format is not yet implemented");
            }

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

            // Parse bbox parameter if provided (format: "minx,miny,maxx,maxy")
            SpatialFilter? spatialFilter = null;
            if (!string.IsNullOrWhiteSpace(bbox))
            {
                try
                {
                    spatialFilter = ParseBboxParameter(bbox, geometryConverter);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest($"Invalid bbox parameter: {ex.Message}");
                }
            }

            // Parse datetime parameter if provided (ISO 8601 format or interval)
            // TODO: Implement temporal filtering based on datetime parameter
            // For now, log a warning if datetime is provided but not yet supported
            if (!string.IsNullOrWhiteSpace(datetime))
            {
                // TODO: Implement temporal filtering
                // This would require adding temporal filter support to FeatureQuery
                // and the underlying data store implementation
                return TypedResults.BadRequest("Datetime parameter filtering is not yet implemented");
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
                    SpatialFilter = spatialFilter,
                    Limit = limit ?? 10, // OGC spec default limit is 10, not 1000
                    Offset = offset
                };
            }
            else
            {
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SpatialFilter = spatialFilter,
                    Limit = limit ?? 10, // OGC spec default limit is 10, not 1000
                    Offset = offset
                };
            }

            // Query features
            var result = await featureStore.QueryAsync(layerId, featureQuery, cancellationToken);

            // Return format-specific response
            return outputFormat switch
            {
                "json" => await FormatAsJsonResponse(result, layer, geometryConverter, context, collectionId, limit, offset),
                "geojson" => await FormatAsGeoJsonResponse(result, layer, geometryConverter, context, collectionId, limit, offset),
                _ => await FormatAsGeoJsonResponse(result, layer, geometryConverter, context, collectionId, limit, offset) // Default to GeoJSON
            };
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
    /// Determines the output format based on the 'f' parameter and Accept header
    /// </summary>
    private static (string format, string contentType) DetermineOutputFormat(HttpContext context, string? f)
    {
        // Format parameter takes precedence over Accept header
        if (!string.IsNullOrWhiteSpace(f))
        {
            return f.ToLowerInvariant() switch
            {
                "json" => ("json", MediaTypes.Json),
                "geojson" => ("geojson", MediaTypes.GeoJson),
                "html" => ("html", "text/html"),
                _ => ("geojson", MediaTypes.GeoJson) // Default to GeoJSON for invalid formats
            };
        }

        // Check Accept header for content negotiation
        var request = context.Request;
        var acceptHeader = request.Headers["Accept"].FirstOrDefault();
        
        if (!string.IsNullOrWhiteSpace(acceptHeader))
        {
            // Simple content negotiation - in practice, you'd want more sophisticated parsing
            if (acceptHeader.Contains("application/geo+json", StringComparison.OrdinalIgnoreCase))
            {
                return ("geojson", MediaTypes.GeoJson);
            }
            
            if (acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ("json", MediaTypes.Json);
            }
            
            if (acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return ("html", "text/html");
            }
        }

        // Default to GeoJSON (OGC API Features default)
        return ("geojson", MediaTypes.GeoJson);
    }

    /// <summary>
    /// Formats the response as GeoJSON FeatureCollection
    /// </summary>
    private static async Task<IResult> FormatAsGeoJsonResponse(
        QueryResult<Feature> result,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        HttpContext context,
        string collectionId,
        int? limit,
        int? offset)
    {
        var featureCollection = ConvertToGeoJsonFeatureCollection(result, layer, geometryConverter, context, collectionId, limit, offset);
        return Results.Json(featureCollection, OgcJsonContext.Default.FeatureCollection, contentType: MediaTypes.GeoJson);
    }

    /// <summary>
    /// Formats the response as plain JSON (simplified format without GeoJSON geometry)
    /// </summary>
    private static async Task<IResult> FormatAsJsonResponse(
        QueryResult<Feature> result,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        HttpContext context,
        string collectionId,
        int? limit,
        int? offset)
    {
        // Create a simplified JSON response (without full GeoJSON geometry objects)
        var features = result.Items.Select(feature => new
        {
            id = feature.Id,
            properties = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            geometry = feature.Geometry != null ? "Present" : null // Simplified geometry indication
        }).ToArray();

        var links = GeneratePagingLinks(context, collectionId, limit, offset, result.TotalCount, result.Items.Length);

        var response = new
        {
            type = "FeatureCollection",
            features = features,
            numberMatched = result.TotalCount,
            numberReturned = result.Items.Length,
            timeStamp = DateTimeOffset.UtcNow,
            links = links.Select(link => new
            {
                href = link.Href,
                rel = link.Rel,
                type = link.Type,
                title = link.Title
            }).ToArray()
        };

        return Results.Json(response, contentType: MediaTypes.Json);
    }

    /// <summary>
    /// Formats a single feature as GeoJSON
    /// </summary>
    private static async Task<IResult> FormatSingleFeatureAsGeoJsonResponse(
        Feature feature,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter)
    {
        var geoJsonFeature = new GeoJsonFeature
        {
            Type = "Feature",
            Id = feature.Id,
            Geometry = ConvertFeatureGeometry(feature.Geometry, geometryConverter),
            Properties = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };

        return Results.Json(geoJsonFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
    }

    /// <summary>
    /// Formats a single feature as simplified JSON
    /// </summary>
    private static async Task<IResult> FormatSingleFeatureAsJsonResponse(
        Feature feature,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter)
    {
        var response = new
        {
            id = feature.Id,
            properties = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            geometry = feature.Geometry != null ? "Present" : null // Simplified geometry indication
        };

        return Results.Json(response, contentType: MediaTypes.Json);
    }

    /// <summary>
    /// Handles the OGC API Features single item request
    /// </summary>
    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string featureId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        string? f = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Determine output format
            var (outputFormat, contentType) = DetermineOutputFormat(context, f);
            
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

            // Parse feature ID
            if (!long.TryParse(featureId, out var featureIdLong))
            {
                return TypedResults.BadRequest("Invalid feature ID format");
            }

            // Get the feature
            var feature = await featureStore.GetAsync(layerId, featureIdLong, cancellationToken);
            if (feature == null)
            {
                return TypedResults.NotFound();
            }

            // Return format-specific response
            return outputFormat switch
            {
                "html" => TypedResults.BadRequest("HTML format is not yet implemented"),
                "json" => await FormatSingleFeatureAsJsonResponse(feature, geometryConverter),
                "geojson" => await FormatSingleFeatureAsGeoJsonResponse(feature, geometryConverter),
                _ => await FormatSingleFeatureAsGeoJsonResponse(feature, geometryConverter) // Default to GeoJSON
            };
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest($"Invalid request: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.BadRequest($"Invalid operation: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Server errors - log details but don't expose to client
            // TODO: Add proper logging here (ex: logger.LogError(ex, "Error processing OGC single item request"))
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while processing the request.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Parses OGC API Features bbox parameter into a SpatialFilter
    /// </summary>
    /// <param name="bbox">Bbox parameter in format "minx,miny,maxx,maxy"</param>
    /// <param name="geometryConverter">Geometry converter service</param>
    /// <returns>SpatialFilter for bbox intersection queries</returns>
    private static SpatialFilter ParseBboxParameter(string bbox, Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter)
    {
        // Parse bbox format: "minx,miny,maxx,maxy"
        var bboxParts = bbox.Split(',');
        if (bboxParts.Length != 4)
        {
            throw new ArgumentException("Bbox parameter must contain exactly 4 comma-separated values: minx,miny,maxx,maxy");
        }

        if (!double.TryParse(bboxParts[0], out var minx) ||
            !double.TryParse(bboxParts[1], out var miny) ||
            !double.TryParse(bboxParts[2], out var maxx) ||
            !double.TryParse(bboxParts[3], out var maxy))
        {
            throw new ArgumentException("Bbox parameter values must be valid numbers");
        }

        // Validate bbox bounds
        if (minx >= maxx || miny >= maxy)
        {
            throw new ArgumentException("Invalid bbox: minimum values must be less than maximum values");
        }

        // Validate coordinate ranges (assuming WGS84/CRS84)
        if (minx < -180 || maxx > 180 || miny < -90 || maxy > 90)
        {
            throw new ArgumentException("Bbox coordinates are out of valid range for CRS84 (-180 to 180 for longitude, -90 to 90 for latitude)");
        }

        // Convert to Esri envelope format for the geometry converter
        var envelopeJson = $"{{\"xmin\":{minx},\"ymin\":{miny},\"xmax\":{maxx},\"ymax\":{maxy}}}";
        
        try
        {
            // Use the geometry converter to create WKB from the envelope
            var wkbGeometry = geometryConverter.ConvertEsriJsonToWkb(envelopeJson);

            return new SpatialFilter
            {
                Geometry = wkbGeometry,
                SpatialRelationship = SpatialRelationship.Intersects
            };
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to create spatial filter from bbox: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Converts query results to GeoJSON FeatureCollection
    /// </summary>
    private static FeatureCollection ConvertToGeoJsonFeatureCollection(
        QueryResult<Feature> queryResult,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        HttpContext httpContext,
        string collectionId,
        int? limit,
        int? offset)
    {
        var features = queryResult.Items.Select(feature => new GeoJsonFeature
        {
            Type = "Feature",
            Id = feature.Id,
            Geometry = ConvertFeatureGeometry(feature.Geometry, geometryConverter),
            Properties = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToArray();

        // Generate paging links
        var links = GeneratePagingLinks(httpContext, collectionId, limit, offset, queryResult.TotalCount, queryResult.Items.Length);

        return new FeatureCollection
        {
            Type = "FeatureCollection",
            Features = features,
            NumberMatched = queryResult.TotalCount,
            NumberReturned = queryResult.Items.Length,
            TimeStamp = DateTimeOffset.UtcNow,
            Links = links
        };
    }

    /// <summary>
    /// Generates paging links for OGC API Features responses
    /// </summary>
    private static ImmutableArray<Link> GeneratePagingLinks(
        HttpContext httpContext,
        string collectionId,
        int? requestedLimit,
        int? requestedOffset,
        long totalCount,
        int returnedCount)
    {
        var request = httpContext.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var basePath = $"{baseUrl}/ogc/features/collections/{collectionId}/items";
        
        var links = new List<Link>();
        
        // Parse query parameters to preserve filters
        var queryParams = new Dictionary<string, string>();
        foreach (var param in request.Query)
        {
            if (param.Key != "limit" && param.Key != "offset")
            {
                queryParams[param.Key] = param.Value.ToString();
            }
        }

        var effectiveLimit = requestedLimit ?? 10; // Default OGC limit
        var effectiveOffset = requestedOffset ?? 0;

        // Helper to build URL with query parameters
        string BuildUrl(int? limit, int? offset)
        {
            var queryBuilder = new List<string>();
            
            foreach (var kvp in queryParams)
            {
                if (!string.IsNullOrEmpty(kvp.Value))
                {
                    queryBuilder.Add($"{kvp.Key}={Uri.EscapeDataString(kvp.Value)}");
                }
            }
            
            if (limit.HasValue)
                queryBuilder.Add($"limit={limit}");
                
            if (offset.HasValue && offset > 0)
                queryBuilder.Add($"offset={offset}");
                
            return queryBuilder.Count > 0 ? $"{basePath}?{string.Join("&", queryBuilder)}" : basePath;
        }

        // Self link (current request)
        links.Add(Link.Create(
            href: BuildUrl(effectiveLimit, effectiveOffset),
            rel: RelationTypes.Self,
            type: MediaTypes.GeoJson,
            title: "This document"
        ));

        // Next link (if there are more results)
        var nextOffset = effectiveOffset + returnedCount;
        if (nextOffset < totalCount)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, nextOffset),
                rel: "next",
                type: MediaTypes.GeoJson,
                title: "Next page"
            ));
        }

        // Previous link (if not on first page)
        if (effectiveOffset > 0)
        {
            var prevOffset = Math.Max(0, effectiveOffset - effectiveLimit);
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, prevOffset > 0 ? prevOffset : null),
                rel: "prev",
                type: MediaTypes.GeoJson,
                title: "Previous page"
            ));
        }

        // First page link (if not on first page)
        if (effectiveOffset > 0)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, null), // offset=0 is implied when null
                rel: "first",
                type: MediaTypes.GeoJson,
                title: "First page"
            ));
        }

        // Last page link (if not on last page and we know the total count)
        if (totalCount > 0 && nextOffset < totalCount)
        {
            // Calculate the offset for the last page
            var lastPageOffset = ((totalCount - 1) / effectiveLimit) * effectiveLimit;
            if (lastPageOffset != effectiveOffset)
            {
                links.Add(Link.Create(
                    href: BuildUrl(effectiveLimit, lastPageOffset > 0 ? lastPageOffset : null),
                    rel: "last",
                    type: MediaTypes.GeoJson,
                    title: "Last page"
                ));
            }
        }

        return links.ToImmutableArray();
    }

    /// <summary>
    /// Converts a feature's WKB geometry to GeoJSON format
    /// </summary>
    private static SimpleGeoJsonGeometry? ConvertFeatureGeometry(byte[]? wkbGeometry, Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter)
    {
        if (wkbGeometry == null || wkbGeometry.Length == 0)
        {
            return null;
        }

        try
        {
            var geoJson = geometryConverter.ConvertWkbToGeoJson(wkbGeometry);
            if (geoJson == null)
            {
                return null;
            }

            if (geoJson is string geoJsonString)
            {
                using var document = JsonDocument.Parse(geoJsonString);
                return BuildSimpleGeometry(document.RootElement);
            }

            if (geoJson is JsonElement element)
            {
                return BuildSimpleGeometry(element);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static SimpleGeoJsonGeometry? BuildSimpleGeometry(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return null;
        }

        var type = typeElement.GetString();
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (!root.TryGetProperty("coordinates", out var coordinatesElement))
        {
            return null;
        }

        return new SimpleGeoJsonGeometry
        {
            Type = type,
            CoordinatesJson = coordinatesElement.GetRawText()
        };
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

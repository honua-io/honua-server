// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.OgcFeatures.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

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
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Features Conformance")
            .WithName("OgcConformance")
            .WithSummary("Get OGC API Features conformance declaration")
            .WithDescription("Conformance classes that this API conforms to")
            .WithTags("OGC API Features")
            .CacheOutput("OgcConformance")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
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
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Features Collection")
            .WithName("CollectionInfo")
            .WithSummary("Get OGC API Features collection metadata")
            .WithDescription("Get detailed metadata for a specific collection")
            .WithTags("OGC API Features")
            .CacheOutput("OgcCollection")
            .Produces<CollectionInfo>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Features Items")
            .WithName("GetItems")
            .WithSummary("Get features from a collection")
            .WithDescription("Get features from a collection with optional filtering using CQL2-Text")
            .WithTags("OGC API Features")
            .Produces<FeatureCollection>(200, MediaTypes.GeoJson)
            .Produces<FeatureCollection>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/features/collections/{collectionId}/items/{featureId}", HandleGetItem)
            .WithDisplayName("OGC API Features Item")
            .WithName("GetItem")
            .WithSummary("Get a specific feature from a collection")
            .WithDescription("Get a specific feature by its ID from a collection")
            .WithTags("OGC API Features")
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.Json)
            .Produces<string>(200, MediaTypes.Html)
            .Produces(404)
            .Produces(400);

        // OGC API Features Transaction Support (Issue #18)
        endpoints.MapPost("/ogc/features/collections/{collectionId}/items", HandleCreateFeature)
            .WithDisplayName("OGC API Features Create")
            .WithName("CreateFeature")
            .WithSummary("Create a new feature in a collection")
            .WithDescription("Add a new feature to the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(201, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404)
            .Produces(409); // Conflict if feature ID already exists

        endpoints.MapPut("/ogc/features/collections/{collectionId}/items/{featureId}", HandleUpdateFeature)
            .WithDisplayName("OGC API Features Update")
            .WithName("UpdateFeature")
            .WithSummary("Update an existing feature")
            .WithDescription("Replace an existing feature with new data")
            .WithTags("OGC API Features", "Transactions")
            .Accepts<GeoJsonFeature>(MediaTypes.GeoJson)
            .Produces<GeoJsonFeature>(200, MediaTypes.GeoJson)
            .Produces(201) // If feature didn't exist (upsert behavior)
            .Produces(400)
            .Produces(404);

        endpoints.MapDelete("/ogc/features/collections/{collectionId}/items/{featureId}", HandleDeleteFeature)
            .WithDisplayName("OGC API Features Delete")
            .WithName("DeleteFeature")
            .WithSummary("Delete a feature from a collection")
            .WithDescription("Remove a feature from the specified collection")
            .WithTags("OGC API Features", "Transactions")
            .Produces(204) // No Content - successful deletion
            .Produces(404)
            .Produces(400);

        return endpoints;
    }

    private static class AllowedQueryParameters
    {
        public static readonly ISet<string> Metadata = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> Items = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f",
            "bbox",
            "datetime",
            "limit",
            "offset",
            "filter",
            "filter-lang"
        };

        public static readonly ISet<string> Item = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> OpenApi = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "f"
        };

        public static readonly ISet<string> Transactions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static BadRequest<string>? ValidateQueryParameters(HttpRequest request, ISet<string> allowedParameters)
    {
        foreach (var key in request.Query.Keys)
        {
            if (!allowedParameters.Contains(key))
            {
                return TypedResults.BadRequest($"Unknown query parameter: {key}");
            }
        }

        return null;
    }

    private static bool TryGetOutputFormat(
        string? formatParameter,
        HttpContext context,
        bool isFeatureContent,
        out string outputFormat,
        out IResult? error)
    {
        outputFormat = isFeatureContent ? MediaTypes.GeoJson : MediaTypes.Json;
        error = null;

        if (!string.IsNullOrWhiteSpace(formatParameter))
        {
            var normalized = formatParameter.Trim();
            switch (normalized.ToLowerInvariant())
            {
                case "json":
                    outputFormat = MediaTypes.Json;
                    return true;
                case "geojson" when isFeatureContent:
                    outputFormat = MediaTypes.GeoJson;
                    return true;
                case "geojson":
                    error = TypedResults.BadRequest("GeoJSON format is only supported for feature content");
                    return false;
                case "html":
                    outputFormat = MediaTypes.Html;
                    return true;
                default:
                    error = TypedResults.BadRequest($"Unsupported format '{formatParameter}'");
                    return false;
            }
        }

        var acceptHeader = context.Request.Headers.Accept.ToString();
        if (string.IsNullOrWhiteSpace(acceptHeader))
        {
            return true;
        }

        var acceptsGeoJson = acceptHeader.Contains("application/geo+json", StringComparison.OrdinalIgnoreCase);
        var acceptsJson = acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        var acceptsJsonSuffix = acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase);
        var acceptsHtml = acceptHeader.Contains("text/html", StringComparison.OrdinalIgnoreCase);

        if (isFeatureContent)
        {
            if (acceptsGeoJson)
            {
                outputFormat = MediaTypes.GeoJson;
                return true;
            }

            if (acceptsJson || acceptsJsonSuffix)
            {
                outputFormat = MediaTypes.Json;
                return true;
            }

            if (acceptsHtml)
            {
                outputFormat = MediaTypes.Html;
                return true;
            }
        }
        else
        {
            if (acceptsJson || acceptsJsonSuffix)
            {
                outputFormat = MediaTypes.Json;
                return true;
            }

            if (acceptsHtml)
            {
                outputFormat = MediaTypes.Html;
                return true;
            }
        }

        if (acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = Results.StatusCode(StatusCodes.Status406NotAcceptable);
        return false;
    }

    /// <summary>
    /// Handles the OGC API Features landing page request
    /// </summary>
    private static IResult HandleGetLandingPage(HttpContext context, string? f)
    {
        var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";
        var selfHref = $"{baseUrl}/ogc/features{request.QueryString}";

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Features",
            Description = "OGC API Features implementation for geospatial data access",
            Links = ImmutableArray.Create(
                // Self link
                Link.Create(
                    href: selfHref,
                    rel: RelationTypes.Self,
                    type: outputFormat,
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

        return FormatMetadataResponse(landingPage, OgcJsonContext.Default.LandingPage, outputFormat, "Landing page");
    }

    /// <summary>
    /// Handles the OGC API Features conformance declaration request
    /// </summary>
    private static IResult HandleGetConformance(HttpContext context, string? f)
    {
        var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Metadata);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
        {
            return formatError!;
        }

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
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/json",
                "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/html",
                "http://www.opengis.net/spec/ogcapi-common-2/1.0/conf/collections"
            )
        };

        return FormatMetadataResponse(conformance, OgcJsonContext.Default.ConformanceDeclaration, outputFormat, "Conformance");
    }

    /// <summary>
    /// Handles the OpenAPI 3.0 specification request
    /// </summary>
    private static IResult HandleGetOpenApiSpec(HttpContext context, string? f)
    {
        var request = context.Request;

        var validationError = ValidateQueryParameters(request, AllowedQueryParameters.OpenApi);
        if (validationError is not null)
        {
            return validationError;
        }

        if (!string.IsNullOrWhiteSpace(f) && !string.Equals(f, "json", StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest($"Unsupported format '{f}'");
        }

        var acceptHeader = request.Headers.Accept.ToString();
        if (!string.IsNullOrWhiteSpace(acceptHeader) &&
            !acceptHeader.Contains("*/*", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/vnd.oai.openapi+json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("application/json", StringComparison.OrdinalIgnoreCase) &&
            !acceptHeader.Contains("+json", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status406NotAcceptable);
        }

        var baseUrl = $"{request.Scheme}://{request.Host}";

        static Dictionary<string, object?> Ref(string refId)
            => new() { ["$ref"] = refId };

        static Dictionary<string, object?> StringSchema()
            => new() { ["type"] = "string" };

        static Dictionary<string, object?> ArraySchema(object items)
            => new() { ["type"] = "array", ["items"] = items };

        var openApiSpec = new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "Honua OGC API Features",
                ["description"] = "OGC API Features implementation for geospatial data access",
                ["version"] = "1.0.0",
                ["contact"] = new Dictionary<string, object?>
                {
                    ["name"] = "Honua Server",
                    ["url"] = baseUrl
                }
            },
            ["servers"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["url"] = baseUrl,
                    ["description"] = "Honua Server"
                }
            },
            ["paths"] = new Dictionary<string, object?>
            {
                ["/ogc/features"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get OGC API Features landing page",
                        ["description"] = "The landing page provides links to the API definition and other resources",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "html" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Landing page information",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/LandingPage")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            }
                        }
                    }
                },
                ["/ogc/features/conformance"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get OGC API Features conformance declaration",
                        ["description"] = "Conformance classes that this API conforms to",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "html" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Conformance declaration",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/ConformanceDeclaration")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            }
                        }
                    }
                },
                ["/ogc/features/collections"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get OGC API Features collections",
                        ["description"] = "Lists all available feature collections",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "html" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Collections list",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/Collections")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            }
                        }
                    }
                },
                ["/ogc/features/collections/{collectionId}"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get OGC API Features collection metadata",
                        ["description"] = "Get detailed metadata for a specific collection",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "html" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Collection metadata",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/CollectionInfo")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Collection not found"
                            }
                        }
                    }
                },
                ["/ogc/features/collections/{collectionId}/items"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get features from a collection",
                        ["description"] = "Get features from a collection with optional filtering",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json, geojson, or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "geojson", "html" }
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "limit",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Maximum number of features to return (default: 10, max: 10000)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "integer",
                                    ["minimum"] = 1,
                                    ["maximum"] = 10000,
                                    ["default"] = 10
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "offset",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Number of features to skip",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "integer",
                                    ["minimum"] = 0,
                                    ["default"] = 0
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "bbox",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Bounding box as 'minx,miny,maxx,maxy' or 'minx,miny,minz,maxx,maxy,maxz'",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "datetime",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Temporal filter (RFC 3339 date-time or interval)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "filter",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "CQL2-Text filter expression",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "filter-lang",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Filter language (only cql2-text is supported)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "cql2-text" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "GeoJSON FeatureCollection",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/geo+json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/FeatureCollection")
                                    },
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/FeatureCollection")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            },
                            ["400"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Bad request (invalid parameters)"
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Collection not found"
                            }
                        }
                    },
                    ["post"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Create a new feature in a collection",
                        ["description"] = "Add a new feature to the specified collection",
                        ["tags"] = new[] { "OGC API Features", "Transactions" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            }
                        },
                        ["requestBody"] = new Dictionary<string, object?>
                        {
                            ["required"] = true,
                            ["content"] = new Dictionary<string, object?>
                            {
                                ["application/geo+json"] = new Dictionary<string, object?>
                                {
                                    ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["201"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature created",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/geo+json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                    },
                                    ["application/json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                    },
                                    ["text/html"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = StringSchema()
                                    }
                                }
                            },
                            ["400"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Bad request"
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Collection not found"
                            },
                            ["409"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Conflict"
                            }
                        }
                    }
                },
                ["/ogc/features/collections/{collectionId}/items/{featureId}"] = new Dictionary<string, object?>
                {
                    ["get"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Get a specific feature from a collection",
                        ["description"] = "Get a specific feature by its ID from a collection",
                        ["tags"] = new[] { "OGC API Features" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "featureId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Feature identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "f",
                                ["in"] = "query",
                                ["required"] = false,
                                ["description"] = "Output format (json, geojson, or html)",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string",
                                    ["enum"] = new[] { "json", "geojson", "html" }
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "GeoJSON Feature",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/geo+json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                    }
                                }
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature or collection not found"
                            }
                        }
                    },
                    ["put"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Replace an existing feature",
                        ["description"] = "Replace an existing feature with new data",
                        ["tags"] = new[] { "OGC API Features", "Transactions" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "featureId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Feature identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            }
                        },
                        ["requestBody"] = new Dictionary<string, object?>
                        {
                            ["required"] = true,
                            ["content"] = new Dictionary<string, object?>
                            {
                                ["application/geo+json"] = new Dictionary<string, object?>
                                {
                                    ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["200"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature updated",
                                ["content"] = new Dictionary<string, object?>
                                {
                                    ["application/geo+json"] = new Dictionary<string, object?>
                                    {
                                        ["schema"] = Ref("#/components/schemas/GeoJsonFeature")
                                    }
                                }
                            },
                            ["400"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Bad request"
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature or collection not found"
                            }
                        }
                    },
                    ["delete"] = new Dictionary<string, object?>
                    {
                        ["summary"] = "Delete a feature",
                        ["description"] = "Remove a feature from the specified collection",
                        ["tags"] = new[] { "OGC API Features", "Transactions" },
                        ["parameters"] = new object[]
                        {
                            new Dictionary<string, object?>
                            {
                                ["name"] = "collectionId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Collection identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            },
                            new Dictionary<string, object?>
                            {
                                ["name"] = "featureId",
                                ["in"] = "path",
                                ["required"] = true,
                                ["description"] = "Feature identifier",
                                ["schema"] = new Dictionary<string, object?>
                                {
                                    ["type"] = "string"
                                }
                            }
                        },
                        ["responses"] = new Dictionary<string, object?>
                        {
                            ["204"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature deleted"
                            },
                            ["404"] = new Dictionary<string, object?>
                            {
                                ["description"] = "Feature or collection not found"
                            }
                        }
                    }
                }
            },
            ["components"] = new Dictionary<string, object?>
            {
                ["schemas"] = new Dictionary<string, object?>
                {
                    ["LandingPage"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["title"] = StringSchema(),
                            ["description"] = StringSchema(),
                            ["links"] = ArraySchema(Ref("#/components/schemas/Link"))
                        }
                    },
                    ["ConformanceDeclaration"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["conformsTo"] = ArraySchema(StringSchema())
                        }
                    },
                    ["Collections"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["collections"] = ArraySchema(Ref("#/components/schemas/CollectionInfo")),
                            ["links"] = ArraySchema(Ref("#/components/schemas/Link"))
                        }
                    },
                    ["CollectionInfo"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["id"] = StringSchema(),
                            ["title"] = StringSchema(),
                            ["description"] = StringSchema(),
                            ["itemType"] = StringSchema(),
                            ["crs"] = ArraySchema(StringSchema()),
                            ["extent"] = Ref("#/components/schemas/Extent"),
                            ["links"] = ArraySchema(Ref("#/components/schemas/Link"))
                        }
                    },
                    ["FeatureCollection"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["type"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "FeatureCollection" }
                            },
                            ["features"] = ArraySchema(Ref("#/components/schemas/GeoJsonFeature")),
                            ["numberMatched"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer"
                            },
                            ["numberReturned"] = new Dictionary<string, object?>
                            {
                                ["type"] = "integer"
                            },
                            ["timeStamp"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["format"] = "date-time"
                            },
                            ["links"] = ArraySchema(Ref("#/components/schemas/Link"))
                        }
                    },
                    ["GeoJsonFeature"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["type"] = new Dictionary<string, object?>
                            {
                                ["type"] = "string",
                                ["enum"] = new[] { "Feature" }
                            },
                            ["id"] = new Dictionary<string, object?>(),
                            ["geometry"] = new Dictionary<string, object?>(),
                            ["properties"] = new Dictionary<string, object?>
                            {
                                ["type"] = "object"
                            },
                            ["links"] = ArraySchema(Ref("#/components/schemas/Link"))
                        }
                    },
                    ["Link"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["href"] = StringSchema(),
                            ["rel"] = StringSchema(),
                            ["type"] = StringSchema(),
                            ["title"] = StringSchema()
                        },
                        ["required"] = new[] { "href", "rel" }
                    },
                    ["Extent"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["spatial"] = Ref("#/components/schemas/SpatialExtent"),
                            ["temporal"] = Ref("#/components/schemas/TemporalExtent")
                        }
                    },
                    ["SpatialExtent"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["bbox"] = ArraySchema(ArraySchema(new Dictionary<string, object?>
                            {
                                ["type"] = "number"
                            })),
                            ["crs"] = StringSchema()
                        }
                    },
                    ["TemporalExtent"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["interval"] = ArraySchema(ArraySchema(StringSchema())),
                            ["trs"] = StringSchema()
                        }
                    }
                }
            }
        };
        return Results.Json(openApiSpec, contentType: MediaTypes.OpenApi);
    }

    /// <summary>
    /// Handles the OGC API Features collections list request
    /// </summary>
    private static async Task<IResult> HandleGetCollections(
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = ValidateQueryParameters(request, AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            var layers = await layerCatalog.ListLayersAsync();
            var collections = layers.Select(layer => CreateCollection(layer, baseUrl)).ToImmutableArray();

            var selfHref = $"{baseUrl}/ogc/features/collections{request.QueryString}";
            var response = new Collections
            {
                CollectionList = collections,
                Links = ImmutableArray.Create(
                    // Self link
                    Link.Create(
                        href: selfHref,
                        rel: RelationTypes.Self,
                        type: outputFormat,
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

            return FormatMetadataResponse(response, OgcJsonContext.Default.Collections, outputFormat, "Collections");
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
        string collectionId,
        HttpContext context,
        string? f,
        ILayerCatalog layerCatalog)
    {
        var request = context.Request;
        var baseUrl = $"{request.Scheme}://{request.Host}";

        try
        {
            var validationError = ValidateQueryParameters(request, AllowedQueryParameters.Metadata);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: false, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

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
            var selfHref = $"{baseUrl}/ogc/features/collections/{collectionId}{request.QueryString}";
            var updatedLinks = collection.Links.Select(link =>
                    string.Equals(link.Rel, RelationTypes.Self, StringComparison.OrdinalIgnoreCase)
                        ? link with { Href = selfHref, Type = outputFormat }
                        : link)
                .ToImmutableArray();

            collection = collection with { Links = updatedLinks };
            return FormatMetadataResponse(collection, OgcJsonContext.Default.CollectionInfo, outputFormat, "Collection");
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
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Items);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return formatError!;
            }

            var filterLang = context.Request.Query["filter-lang"].ToString();
            if (!string.IsNullOrWhiteSpace(filterLang) &&
                !string.Equals(filterLang, "cql2-text", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("Only filter-lang=cql2-text is supported");
            }

            if (string.IsNullOrWhiteSpace(filter) && !string.IsNullOrWhiteSpace(filterLang))
            {
                return TypedResults.BadRequest("filter-lang requires a filter parameter");
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
            var effectiveLimit = limit ?? 10;
            if (effectiveLimit > maxLimit)
            {
                effectiveLimit = maxLimit;
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
                    spatialFilter = ParseBboxParameter(bbox);
                }
                catch (ArgumentException ex)
                {
                    return TypedResults.BadRequest($"Invalid bbox parameter: {ex.Message}");
                }
            }

            // Parse datetime parameter if provided (ISO 8601 format or interval)
            TemporalFilter? temporalFilter = null;
            if (!TryBuildTemporalFilter(datetime, layer, out temporalFilter, out var temporalError))
            {
                return TypedResults.BadRequest(temporalError);
            }

            var whereClause = string.IsNullOrWhiteSpace(filter) ? null : filter;
            var includeNullGeometry = false;

            // Create feature query with proper parameterized filter support
            FeatureQuery featureQuery;
            if (filterExpression != null)
            {
                var translator = new SqlFilterTranslator(
                    useJsonAttributes: true,
                    attributesColumn: "attributes",
                    geometryColumn: "geometry",
                    primaryKeyColumn: "objectid");
                var sqlFragment = translator.Translate(filterExpression, layer);

                // Use parameterized SQL filter to preserve parameters
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SqlFilter = sqlFragment,
                    SpatialFilter = spatialFilter,
                    TemporalFilter = temporalFilter,
                    IncludeNullGeometry = includeNullGeometry,
                    Limit = effectiveLimit, // OGC spec default limit is 10, not 1000
                    Offset = offset
                };
            }
            else
            {
                featureQuery = new FeatureQuery
                {
                    Where = whereClause,
                    SpatialFilter = spatialFilter,
                    TemporalFilter = temporalFilter,
                    IncludeNullGeometry = includeNullGeometry,
                    Limit = effectiveLimit, // OGC spec default limit is 10, not 1000
                    Offset = offset
                };
            }

            // Query features
            var result = await featureStore.QueryAsync(layerId, featureQuery, cancellationToken);

            // Convert to GeoJSON FeatureCollection with enhanced metadata
            var featureCollection = ConvertToGeoJsonFeatureCollection(result, layer, geometryConverter, context, collectionId, effectiveLimit, offset, outputFormat);

            // Return response in requested format
            return FormatFeatureCollectionResponse(featureCollection, outputFormat);
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
    /// Handles the OGC API Features single item request
    /// </summary>
    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string featureId,
        string? f,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Item);
            if (validationError is not null)
            {
                return validationError;
            }

            if (!TryGetOutputFormat(f, context, isFeatureContent: true, out var outputFormat, out var formatError))
            {
                return formatError!;
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

            // Parse feature ID
            if (!long.TryParse(featureId, out var featureIdLong))
            {
                return TypedResults.NotFound();
            }

            // Get the feature
            var feature = await featureStore.GetAsync(layerId, featureIdLong, cancellationToken);
            if (feature == null)
            {
                return TypedResults.NotFound();
            }

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var selfHref = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{featureId}{context.Request.QueryString}";
            var itemLinks = ImmutableArray.Create(
                Link.Create(
                    href: selfHref,
                    rel: RelationTypes.Self,
                    type: outputFormat,
                    title: "This document"
                ),
                Link.Create(
                    href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                    rel: RelationTypes.Collection,
                    type: MediaTypes.Json,
                    title: "Collection"
                )
            );

            // Convert to GeoJSON feature
            var geoJsonFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = ((Feature)feature).Id,
                Geometry = ConvertFeatureGeometry(((Feature)feature).Geometry, geometryConverter),
                Properties = ((Feature)feature).Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = itemLinks
            };

            // Determine output format and return appropriate response
            return FormatFeatureResponse(geoJsonFeature, outputFormat);
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
    /// <param name="bbox">Bbox parameter in format "minx,miny,maxx,maxy" or "minx,miny,minz,maxx,maxy,maxz"</param>
    /// <returns>SpatialFilter for bbox intersection queries</returns>
    private static SpatialFilter ParseBboxParameter(string bbox)
    {
        // Parse bbox format: "minx,miny,maxx,maxy" or "minx,miny,minz,maxx,maxy,maxz"
        var bboxParts = bbox.Split(',', StringSplitOptions.TrimEntries);
        if (bboxParts.Length is not (4 or 6))
        {
            throw new ArgumentException("Bbox parameter must contain exactly 4 or 6 comma-separated values");
        }

        double minx;
        double miny;
        double maxx;
        double maxy;
        double? minz = null;
        double? maxz = null;

        if (bboxParts.Length == 4)
        {
            if (!double.TryParse(bboxParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minx) ||
                !double.TryParse(bboxParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out miny) ||
                !double.TryParse(bboxParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out maxx) ||
                !double.TryParse(bboxParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out maxy))
            {
                throw new ArgumentException("Bbox parameter values must be valid numbers");
            }
        }
        else
        {
            if (!double.TryParse(bboxParts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minx) ||
                !double.TryParse(bboxParts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out miny) ||
                !double.TryParse(bboxParts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMinz) ||
                !double.TryParse(bboxParts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out maxx) ||
                !double.TryParse(bboxParts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out maxy) ||
                !double.TryParse(bboxParts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaxz))
            {
                throw new ArgumentException("Bbox parameter values must be valid numbers");
            }

            minz = parsedMinz;
            maxz = parsedMaxz;
        }

        // Validate bbox bounds
        if (miny > maxy)
        {
            throw new ArgumentException("Invalid bbox: minimum latitude must be less than or equal to maximum latitude");
        }

        if (minz.HasValue && maxz.HasValue && minz.Value > maxz.Value)
        {
            throw new ArgumentException("Invalid bbox: minimum elevation must be less than or equal to maximum elevation");
        }

        // Validate coordinate ranges (assuming CRS84)
        if (minx < -180 || minx > 180 || maxx < -180 || maxx > 180 || miny < -90 || miny > 90 || maxy < -90 || maxy > 90)
        {
            throw new ArgumentException("Bbox coordinates are out of valid range for CRS84 (-180 to 180 for longitude, -90 to 90 for latitude)");
        }

        var crossesAntimeridian = minx > maxx;

        try
        {
            var wkbGeometry = crossesAntimeridian
                ? CreateAntimeridianBboxWkb(minx, miny, maxx, maxy)
                : CreateBboxWkb(minx, miny, maxx, maxy);

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

    private static byte[] CreateBboxWkb(double minx, double miny, double maxx, double maxy)
    {
        var factory = GeometryFactory.Default;
        var envelope = new Envelope(minx, maxx, miny, maxy);
        var polygon = factory.ToGeometry(envelope);
        var writer = new WKBWriter();
        return writer.Write(polygon);
    }

    private static byte[] CreateAntimeridianBboxWkb(double minx, double miny, double maxx, double maxy)
    {
        var factory = GeometryFactory.Default;
        var left = factory.ToGeometry(new Envelope(minx, 180, miny, maxy)) as Polygon
            ?? throw new ArgumentException("Failed to create antimeridian bbox polygon");
        var right = factory.ToGeometry(new Envelope(-180, maxx, miny, maxy)) as Polygon
            ?? throw new ArgumentException("Failed to create antimeridian bbox polygon");

        var multiPolygon = factory.CreateMultiPolygon(new[] { left, right });
        var writer = new WKBWriter();
        return writer.Write(multiPolygon);
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
        int? offset,
        string outputFormat)
    {
        var features = queryResult.Items.Select(feature => new GeoJsonFeature
        {
            Type = "Feature",
            Id = ((Feature)feature).Id,
            Geometry = ConvertFeatureGeometry(((Feature)feature).Geometry, geometryConverter),
            Properties = ((Feature)feature).Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        }).ToArray();

        // Generate paging links
        var links = GeneratePagingLinks(httpContext, collectionId, limit, offset, queryResult.TotalCount, queryResult.Items.Length, outputFormat);

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
        int returnedCount,
        string outputFormat)
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
            type: outputFormat,
            title: "This document"
        ));

        // Next link (if there are more results)
        var nextOffset = effectiveOffset + returnedCount;
        if (nextOffset < totalCount)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, nextOffset),
                rel: "next",
                type: outputFormat,
                title: "Next page"
            ));
        }

        // Previous link (if not on first page)
        if (effectiveOffset > 0)
        {
            var prevOffset = Math.Max(0, effectiveOffset - effectiveLimit);
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, prevOffset > 0 ? (int?)prevOffset : null),
                rel: "prev",
                type: outputFormat,
                title: "Previous page"
            ));
        }

        // First page link (if not on first page)
        if (effectiveOffset > 0)
        {
            links.Add(Link.Create(
                href: BuildUrl(effectiveLimit, null), // offset=0 is implied when null
                rel: "first",
                type: outputFormat,
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
                    href: BuildUrl(effectiveLimit, lastPageOffset > 0 ? (int?)lastPageOffset : null),
                    rel: "last",
                    type: outputFormat,
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

        if (string.Equals(type, "GeometryCollection", StringComparison.OrdinalIgnoreCase))
        {
            if (!root.TryGetProperty("geometries", out var geometriesElement))
            {
                return null;
            }

            return new SimpleGeoJsonGeometry
            {
                Type = type,
                GeometriesJson = geometriesElement.GetRawText()
            };
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
                rel: RelationTypes.Items,
                type: MediaTypes.GeoJson,
                title: "Items"
            ),

            // Data link (OGC API Features requirement)
            Link.Create(
                href: $"{baseUrl}/ogc/features/collections/{collectionId}/items",
                rel: RelationTypes.Data,
                type: MediaTypes.GeoJson,
                title: "Data"
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

    /// <summary>
    /// Handles the OGC API Features create feature request (POST)
    /// </summary>
    private static async Task<IResult> HandleCreateFeature(
        string collectionId,
        GeoJsonFeature feature,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Validate feature data
            if (!string.Equals(feature.Type, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("GeoJSON type must be 'Feature'");
            }

            // Convert GeoJSON feature to internal Feature format (ignore client-supplied ID)
            var internalFeature = ConvertGeoJsonFeatureToInternal(feature, ignoreId: true);

            // Create the feature
            var createdFeature = await featureStore.CreateAsync(layerId, internalFeature, cancellationToken);

            // Convert back to GeoJSON
            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var responseFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = createdFeature.Id,
                Geometry = ConvertFeatureGeometry(createdFeature.Geometry, geometryConverter),
                Properties = createdFeature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = ImmutableArray.Create(
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}/items/{createdFeature.Id}",
                        rel: RelationTypes.Self,
                        type: MediaTypes.GeoJson,
                        title: "This document"
                    ),
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                        rel: RelationTypes.Collection,
                        type: MediaTypes.Json,
                        title: "Collection"
                    )
                )
            };

            // Return 201 Created with Location header
            context.Response.Headers.Location = $"{baseUrl}/ogc/features/collections/{collectionId}/items/{responseFeature.Id}";
            return Results.Json(responseFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson, statusCode: StatusCodes.Status201Created);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest($"Invalid request: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // Could be duplicate ID or other business logic error
            return TypedResults.Conflict($"Cannot create feature: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Server errors - log details but don't expose to client
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while creating the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features update feature request (PUT)
    /// </summary>
    private static async Task<IResult> HandleUpdateFeature(
        string collectionId,
        string featureId,
        GeoJsonFeature feature,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        Honua.Server.Features.Infrastructure.Services.IGeometryConverter geometryConverter,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Parse feature ID
            if (!long.TryParse(featureId, out var longFeatureId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Validate feature data
            if (!string.Equals(feature.Type, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.BadRequest("GeoJSON type must be 'Feature'");
            }

            // Ensure the feature ID in the URL matches the feature data
            if (feature.Id != null && feature.Id.ToString() != featureId)
            {
                return TypedResults.BadRequest("Feature ID in URL does not match feature ID in data");
            }

            // Check if feature exists (no upsert behavior)
            var existingFeature = await featureStore.GetAsync(layerId, longFeatureId, cancellationToken);
            if (existingFeature == null)
            {
                return TypedResults.NotFound();
            }

            // Convert GeoJSON feature to internal Feature format using the path ID
            var internalFeature = ConvertGeoJsonFeatureToInternal(feature, overrideId: longFeatureId);

            // Update the feature
            var updatedFeature = await featureStore.UpdateAsync(layerId, internalFeature, cancellationToken);

            var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
            var responseFeature = new GeoJsonFeature
            {
                Type = "Feature",
                Id = updatedFeature.Id,
                Geometry = ConvertFeatureGeometry(updatedFeature.Geometry, geometryConverter),
                Properties = updatedFeature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Links = ImmutableArray.Create(
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}/items/{updatedFeature.Id}",
                        rel: RelationTypes.Self,
                        type: MediaTypes.GeoJson,
                        title: "This document"
                    ),
                    Link.Create(
                        href: $"{baseUrl}/ogc/features/collections/{collectionId}",
                        rel: RelationTypes.Collection,
                        type: MediaTypes.Json,
                        title: "Collection"
                    )
                )
            };

            return Results.Json(responseFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
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
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while updating the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Handles the OGC API Features delete feature request (DELETE)
    /// </summary>
    private static async Task<IResult> HandleDeleteFeature(
        string collectionId,
        string featureId,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var validationError = ValidateQueryParameters(context.Request, AllowedQueryParameters.Transactions);
            if (validationError is not null)
            {
                return validationError;
            }

            // Parse collection ID to layer ID
            if (!int.TryParse(collectionId, out var layerId))
            {
                return TypedResults.NotFound();
            }

            // Parse feature ID
            if (!long.TryParse(featureId, out var longFeatureId))
            {
                return TypedResults.NotFound();
            }

            // Get layer information
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return TypedResults.NotFound();
            }

            // Delete the feature
            var deleted = await featureStore.DeleteAsync(layerId, longFeatureId, cancellationToken);

            if (!deleted)
            {
                return TypedResults.NotFound();
            }

            // Return 204 No Content for successful deletion
            return Results.NoContent();
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
            _ = ex; // Suppress unused variable warning until logging is implemented
            return TypedResults.Problem(
                title: "Internal server error",
                detail: "An error occurred while deleting the feature.",
                statusCode: 500);
        }
    }

    /// <summary>
    /// Converts a GeoJSON feature to internal Feature format
    /// </summary>
    private static Feature ConvertGeoJsonFeatureToInternal(
        GeoJsonFeature geoJsonFeature,
        long? overrideId = null,
        bool ignoreId = false)
    {
        byte[]? geometryWkb = null;
        if (geoJsonFeature.Geometry != null)
        {
            geometryWkb = ConvertGeoJsonGeometryToWkb(geoJsonFeature.Geometry);
        }

        var featureId = overrideId ?? (ignoreId ? 0 : ExtractFeatureId(geoJsonFeature));

        var attributes = geoJsonFeature.Properties?.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value) ?? ImmutableDictionary<string, object?>.Empty;

        return Feature.Create(featureId, geometryWkb, attributes);
    }

    private static long ExtractFeatureId(GeoJsonFeature geoJsonFeature)
    {
        if (geoJsonFeature.Id is null)
        {
            throw new ArgumentException("Feature ID is required");
        }

        return geoJsonFeature.Id switch
        {
            long longId => longId,
            int intId => intId,
            string strId when long.TryParse(strId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            JsonElement element => ExtractFeatureId(element),
            _ => throw new ArgumentException("Feature ID must be a valid integer")
        };
    }

    private static long ExtractFeatureId(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException("Feature ID must be a valid integer")
        };
    }

    private static byte[] ConvertGeoJsonGeometryToWkb(SimpleGeoJsonGeometry geometry)
    {
        if (string.IsNullOrWhiteSpace(geometry.Type))
        {
            throw new ArgumentException("Geometry type is required");
        }

        var typeJson = JsonSerializer.Serialize(geometry.Type);
        string geoJson;

        if (string.Equals(geometry.Type, "GeometryCollection", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(geometry.GeometriesJson))
            {
                throw new ArgumentException("GeometryCollection geometries are required");
            }

            geoJson = $"{{\"type\":{typeJson},\"geometries\":{geometry.GeometriesJson}}}";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(geometry.CoordinatesJson))
            {
                throw new ArgumentException("Geometry coordinates are required");
            }

            geoJson = $"{{\"type\":{typeJson},\"coordinates\":{geometry.CoordinatesJson}}}";
        }

        try
        {
            var reader = new GeoJsonReader();
            var parsed = reader.Read<Geometry>(geoJson)
                ?? throw new ArgumentException("Geometry could not be parsed");

            parsed.SRID = 4326;
            var writer = new WKBWriter();
            return writer.Write(parsed);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            throw new ArgumentException($"Invalid GeoJSON geometry: {ex.Message}", ex);
        }
    }

    private static bool TryBuildTemporalFilter(
        string? datetime,
        Honua.Core.Features.Catalog.Domain.LayerDefinition layer,
        out TemporalFilter? temporalFilter,
        out string? error)
    {
        temporalFilter = null;
        error = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            return true;
        }

        if (!TryParseDatetimeParameter(datetime, out var start, out var end, out error))
        {
            return false;
        }

        var temporalField = layer.Fields.FirstOrDefault(field => field.Type == FieldType.DateTime)
            ?? layer.Fields.FirstOrDefault(field => field.Type == FieldType.Date);

        if (temporalField == null)
        {
            return true;
        }

        temporalFilter = new TemporalFilter
        {
            PropertyName = temporalField.Name,
            PropertyType = temporalField.Type == FieldType.Date ? TemporalPropertyType.Date : TemporalPropertyType.DateTime,
            Start = start,
            End = end
        };

        return true;
    }

    private static bool TryParseDatetimeParameter(
        string datetime,
        out DateTimeOffset? start,
        out DateTimeOffset? end,
        out string? error)
    {
        start = null;
        end = null;
        error = null;

        if (string.IsNullOrWhiteSpace(datetime))
        {
            error = "Datetime parameter must not be empty";
            return false;
        }

        var parts = datetime.Split('/', StringSplitOptions.None);
        if (parts.Length == 1)
        {
            if (!TryParseDatetimeValue(parts[0], out var instant, out error))
            {
                return false;
            }

            start = instant;
            end = instant;
            return true;
        }

        if (parts.Length != 2)
        {
            error = "Datetime parameter must be a valid RFC 3339 timestamp or interval";
            return false;
        }

        if (!TryParseDatetimeValue(parts[0], out start, out error))
        {
            return false;
        }

        if (!TryParseDatetimeValue(parts[1], out end, out error))
        {
            return false;
        }

        if (!start.HasValue && !end.HasValue)
        {
            error = "Datetime interval must include a start or end value";
            return false;
        }

        if (start.HasValue && end.HasValue && start.Value > end.Value)
        {
            error = "Datetime interval start must be before end";
            return false;
        }

        return true;
    }

    private static bool TryParseDatetimeValue(
        string value,
        out DateTimeOffset? parsed,
        out string? error)
    {
        parsed = null;
        error = null;

        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "..")
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
            trimmed,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsedValue))
        {
            error = $"Invalid datetime value '{value}'";
            return false;
        }

        parsed = parsedValue;
        return true;
    }

    private static IResult FormatMetadataResponse<T>(
        T payload,
        JsonTypeInfo<T> typeInfo,
        string outputFormat,
        string title)
    {
        if (outputFormat == MediaTypes.Html)
        {
            var json = JsonSerializer.Serialize(payload, typeInfo);
            var html = BuildHtmlDocument(title, json);
            return Results.Text(html, MediaTypes.Html);
        }

        return Results.Json(payload, typeInfo, contentType: MediaTypes.Json);
    }

    private static IResult FormatFeatureCollectionResponse(FeatureCollection featureCollection, string outputFormat)
    {
        if (outputFormat == MediaTypes.Html)
        {
            var json = JsonSerializer.Serialize(featureCollection, OgcJsonContext.Default.FeatureCollection);
            var html = BuildHtmlDocument("Feature collection", json);
            return Results.Text(html, MediaTypes.Html);
        }

        var contentType = outputFormat == MediaTypes.Json ? MediaTypes.Json : MediaTypes.GeoJson;
        return Results.Json(featureCollection, OgcJsonContext.Default.FeatureCollection, contentType: contentType);
    }

    private static IResult FormatFeatureResponse(GeoJsonFeature geoJsonFeature, string outputFormat)
    {
        if (outputFormat == MediaTypes.Html)
        {
            var json = JsonSerializer.Serialize(geoJsonFeature, OgcJsonContext.Default.GeoJsonFeature);
            var html = BuildHtmlDocument("Feature", json);
            return Results.Text(html, MediaTypes.Html);
        }

        var contentType = outputFormat == MediaTypes.Json ? MediaTypes.Json : MediaTypes.GeoJson;
        return Results.Json(geoJsonFeature, OgcJsonContext.Default.GeoJsonFeature, contentType: contentType);
    }

    private static string BuildHtmlDocument(string title, string json)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedJson = WebUtility.HtmlEncode(json);

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<title>{encodedTitle}</title>
<style>
body {{ font-family: Arial, sans-serif; margin: 24px; color: #111; background: #f8f8f8; }}
main {{ max-width: 1200px; margin: 0 auto; background: #fff; padding: 16px 20px; border: 1px solid #ddd; }}
h1 {{ font-size: 20px; margin: 0 0 12px; }}
pre {{ white-space: pre-wrap; word-break: break-word; margin: 0; }}
</style>
</head>
<body>
<main>
<h1>{encodedTitle}</h1>
<pre>{encodedJson}</pre>
</main>
</body>
</html>";
    }
}

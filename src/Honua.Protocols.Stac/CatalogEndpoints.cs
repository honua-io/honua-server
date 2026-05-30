// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Stac.Models;
using Honua.Server.Features.Protocols.Stac.Services;

namespace Honua.Server.Features.Protocols.Stac;

/// <summary>
/// STAC Catalog landing page endpoint.
/// </summary>
internal static class CatalogEndpoints
{
    /// <summary>
    /// Maps the STAC catalog root endpoint.
    /// </summary>
    public static IEndpointRouteBuilder MapStacCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stac", HandleGetCatalog)
            .WithDisplayName("STAC Catalog")
            .WithName("StacCatalog")
            .WithSummary("Get the STAC root catalog")
            .WithDescription("Returns the root STAC catalog with links to child collections and search")
            .WithTags("STAC")
            .CacheOutput("StacCatalog")
            .WithETag()
            .Produces<StacCatalog>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/stac/conformance", HandleGetConformance)
            .WithDisplayName("STAC Conformance")
            .WithName("StacConformance")
            .WithSummary("Get the STAC API conformance declaration")
            .WithDescription("Returns the conformance classes implemented by the STAC API surface")
            .WithTags("STAC")
            .CacheOutput("StacCatalog")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces(404);

        endpoints.MapGet("/stac/openapi.json", HandleGetOpenApiSpec)
            .WithDisplayName("STAC OpenAPI Specification")
            .WithName("StacOpenApiSpec")
            .WithSummary("Get OpenAPI 3.0 specification for STAC API")
            .WithDescription("The OpenAPI specification describes the STAC API endpoints.")
            .WithTags("STAC")
            .CacheOutput("StacCatalog")
            .Produces<object>(200, MediaTypes.OpenApi)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetCatalog(
        HttpContext context,
        ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Catalog,
            "/stac",
            HttpMethods.Get);
        StacLog.CatalogRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Catalog);
        if (validationError is not null)
        {
            StacTelemetry.SetFailed(activity, "invalid_query_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var stacBase = $"{baseUrl}/stac";

            var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(context, cancellationToken)
                .ConfigureAwait(false);

            var links = ImmutableArray.CreateBuilder<Link>();

            // Self
            links.Add(Link.Create(
                href: stacBase,
                rel: RelationTypes.Self,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

            // Root
            links.Add(Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

            // API definition and documentation.
            links.Add(Link.Create(
                href: $"{stacBase}/openapi.json",
                rel: RelationTypes.ServiceDesc,
                type: MediaTypes.OpenApi,
                title: "STAC API definition"));

            links.Add(Link.Create(
                href: "https://api.stacspec.org/v1.0.0/",
                rel: RelationTypes.ServiceDoc,
                type: MediaTypes.Html,
                title: "STAC API documentation"));

            // Conformance
            links.Add(Link.Create(
                href: $"{stacBase}/conformance",
                rel: RelationTypes.Conformance,
                type: MediaTypes.Json,
                title: "Conformance declaration"));

            // Collections
            links.Add(Link.Create(
                href: $"{stacBase}/collections",
                rel: "data",
                type: MediaTypes.Json,
                title: "Collections"));

            // Search
            links.Add(Link.Create(
                href: $"{stacBase}/search",
                rel: StacConstants.StacRelations.Search,
                type: MediaTypes.GeoJson,
                title: "STAC Search"));

            // Child collection links
            foreach (var resolved in visible)
            {
                var collectionId = resolved.LayerIndex.ToString(CultureInfo.InvariantCulture);
                var title = ResolveDisplayName(resolved);
                links.Add(Link.Create(
                    href: $"{stacBase}/collections/{collectionId}",
                    rel: StacConstants.StacRelations.Child,
                    type: MediaTypes.Json,
                    title: title));
            }

            var catalog = new StacCatalog
            {
                Id = StacConstants.CatalogId,
                Title = "Honua STAC Catalog",
                Description = "SpatioTemporal Asset Catalog for Honua geospatial data discovery",
                ConformsTo = GetConformanceClasses(),
                Links = links.ToImmutable()
            };

            StacLog.CatalogReturned(logger, visible.Length);
            StacTelemetry.SetResultCount(activity, visible.Length);
            return Results.Json(catalog, StacJsonContext.Default.StacCatalog, MediaTypes.Json);
        }
        catch (OperationCanceledException ex)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC catalog.");
        }
    }

    private static IResult HandleGetConformance(HttpContext context)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Catalog);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var stacBase = $"{baseUrl}/stac";
        var conformance = new ConformanceDeclaration
        {
            ConformsTo = GetConformanceClasses(),
            Links = ImmutableArray.Create(
                Link.Create(
                    href: $"{stacBase}/conformance",
                    rel: RelationTypes.Self,
                    type: MediaTypes.Json,
                    title: "Conformance declaration"),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"))
        };

        return Results.Json(conformance, OgcJsonContext.Default.ConformanceDeclaration, MediaTypes.Json);
    }

    private static IResult HandleGetOpenApiSpec(HttpContext context)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Catalog);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        var openApi = """
            {
              "openapi": "3.0.3",
              "info": {
                "title": "Honua STAC API",
                "description": "STAC API surface for Honua geospatial discovery.",
                "version": "1.0.0"
              },
              "paths": {
                "/stac": {
                  "get": {
                    "summary": "STAC root catalog",
                    "responses": {
                      "200": {
                        "description": "STAC Catalog"
                      }
                    }
                  }
                },
                "/stac/conformance": {
                  "get": {
                    "summary": "STAC conformance declaration",
                    "responses": {
                      "200": {
                        "description": "Conformance declaration"
                      }
                    }
                  }
                },
                "/stac/collections": {
                  "get": {
                    "summary": "STAC collections",
                    "responses": {
                      "200": {
                        "description": "STAC Collections response"
                      }
                    }
                  }
                },
                "/stac/collections/{collectionId}": {
                  "get": {
                    "summary": "STAC collection",
                    "parameters": [
                      {
                        "name": "collectionId",
                        "in": "path",
                        "required": true,
                        "schema": {
                          "type": "string"
                        }
                      }
                    ],
                    "responses": {
                      "200": {
                        "description": "STAC Collection"
                      }
                    }
                  }
                },
                "/stac/collections/{collectionId}/items": {
                  "get": {
                    "summary": "STAC collection items",
                    "parameters": [
                      {
                        "name": "collectionId",
                        "in": "path",
                        "required": true,
                        "schema": {
                          "type": "string"
                        }
                      }
                    ],
                    "responses": {
                      "200": {
                        "description": "STAC ItemCollection"
                      }
                    }
                  }
                },
                "/stac/collections/{collectionId}/items/{itemId}": {
                  "get": {
                    "summary": "STAC item",
                    "parameters": [
                      {
                        "name": "collectionId",
                        "in": "path",
                        "required": true,
                        "schema": {
                          "type": "string"
                        }
                      },
                      {
                        "name": "itemId",
                        "in": "path",
                        "required": true,
                        "schema": {
                          "type": "string"
                        }
                      }
                    ],
                    "responses": {
                      "200": {
                        "description": "STAC Item"
                      }
                    }
                  }
                },
                "/stac/search": {
                  "get": {
                    "summary": "STAC item search",
                    "responses": {
                      "200": {
                        "description": "STAC ItemCollection"
                      }
                    }
                  },
                  "post": {
                    "summary": "STAC item search",
                    "responses": {
                      "200": {
                        "description": "STAC ItemCollection"
                      }
                    }
                  }
                }
              }
            }
            """;

        return Results.Content(openApi, MediaTypes.OpenApi);
    }

    private static string ResolveDisplayName(StacV2Lookups.ResolvedStacPublication resolved)
    {
        return resolved.Publication.TitleOverride
            ?? resolved.Publication.Metadata.Title
            ?? (string.IsNullOrEmpty(resolved.Publication.Metadata.Name) ? null : resolved.Publication.Metadata.Name)
            ?? resolved.Resource.Metadata.Title
            ?? (string.IsNullOrEmpty(resolved.Resource.Metadata.Name) ? null : resolved.Resource.Metadata.Name)
            ?? resolved.LayerIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static ImmutableArray<string> GetConformanceClasses()
        => ImmutableArray.Create(
            StacConstants.Conformance.Core,
            StacConstants.Conformance.ItemSearch,
            StacConstants.Conformance.OgcApiFeatures,
            StacConstants.Conformance.Collections,
            StacConstants.Conformance.FieldsExtension,
            StacConstants.Conformance.SortExtension,
            StacConstants.Conformance.FilterExtension);
}

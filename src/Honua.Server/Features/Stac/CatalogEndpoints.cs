// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.Stac.Models;
using Honua.Server.Features.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Stac;

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
            .Produces<StacCatalog>(200, MediaTypes.Json)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetCatalog(
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        StacLog.CatalogRequested(logger);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Catalog);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var stacBase = $"{baseUrl}/stac";

            var visibleLayers = await StacFilterHelpers.ResolveStacVisibleLayersAsync(
                context, layerCatalog, cancellationToken);

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

            // Do not advertise the OGC API Features OpenAPI document as the STAC
            // service description. Until STAC has a dedicated API definition, omit
            // this relation rather than publishing a misleading link.

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
            foreach (var layer in visibleLayers)
            {
                var collectionId = layer.Id.ToString(CultureInfo.InvariantCulture);
                links.Add(Link.Create(
                    href: $"{stacBase}/collections/{collectionId}",
                    rel: StacConstants.StacRelations.Child,
                    type: MediaTypes.Json,
                    title: layer.Name));
            }

            var catalog = new StacCatalog
            {
                Id = StacConstants.CatalogId,
                Title = "Honua STAC Catalog",
                Description = "SpatioTemporal Asset Catalog for Honua geospatial data discovery",
                ConformsTo = ImmutableArray.Create(
                    StacConstants.Conformance.Core,
                    StacConstants.Conformance.ItemSearch,
                    StacConstants.Conformance.OgcApiFeatures,
                    StacConstants.Conformance.Collections,
                    StacConstants.Conformance.FieldsExtension,
                    StacConstants.Conformance.SortExtension,
                    StacConstants.Conformance.FilterExtension),
                Links = links.ToImmutable()
            };

            StacLog.CatalogReturned(logger, visibleLayers.Length);
            return Results.Json(catalog, StacJsonContext.Default.StacCatalog, MediaTypes.Json);
        }
        catch (OperationCanceledException)
            when (TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StacLog.OperationFailed(logger, ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context, "An error occurred while retrieving the STAC catalog.");
        }
    }
}

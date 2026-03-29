// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.Stac.Models;
using Honua.Server.Features.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Stac;

/// <summary>
/// STAC item listing and detail endpoints for a collection.
/// </summary>
internal static class ItemEndpoints
{
    /// <summary>
    /// Maps STAC item endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapStacItemEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stac/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("STAC Items")
            .WithName("StacItems")
            .WithSummary("Get items in a STAC collection")
            .WithDescription("Lists items in a collection with optional spatial/temporal filtering")
            .WithTags("STAC")
            .Produces<StacItemCollection>(200, MediaTypes.GeoJson)
            .Produces(404);

        endpoints.MapGet("/stac/collections/{collectionId}/items/{itemId}", HandleGetItem)
            .WithDisplayName("STAC Item")
            .WithName("StacItem")
            .WithSummary("Get a single STAC item")
            .WithDescription("Returns a single STAC item by ID")
            .WithTags("STAC")
            .Produces<StacItem>(200, MediaTypes.GeoJson)
            .Produces(404);

        return endpoints;
    }

    private static async Task<IResult> HandleGetItems(
        string collectionId,
        HttpContext context,
        [FromQuery] int? limit,
        [FromQuery] string? bbox,
        [FromQuery] string? datetime,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        StacLog.ItemsRequested(logger, collectionId, limit);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Items);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId))
            {
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer is null)
            {
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var effectiveLimit = Math.Clamp(limit ?? StacConstants.DefaultSearchLimit, 1, StacConstants.MaxSearchLimit);

            var query = new FeatureQuery
            {
                Limit = effectiveLimit
            };

            // Apply bbox filter
            if (!string.IsNullOrWhiteSpace(bbox))
            {
                var spatialFilter = StacFilterHelpers.ParseBbox(bbox);
                if (spatialFilter is not null)
                {
                    query = query with { SpatialFilter = spatialFilter };
                }
            }

            // Apply datetime filter
            if (!string.IsNullOrWhiteSpace(datetime))
            {
                var temporalFilter = StacFilterHelpers.ParseDatetime(datetime, layer);
                if (temporalFilter is not null)
                {
                    query = query with { TemporalFilter = temporalFilter };
                }
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var result = await featureReader.QueryAsync(layerId, query, cancellationToken);

            var items = result.Features
                .Select(f => StacMappingService.MapFeatureToItem(f, layer, baseUrl))
                .ToImmutableArray();

            var stacBase = $"{baseUrl}/stac";
            var links = ImmutableArray.Create(
                Link.Create(
                    href: $"{stacBase}/collections/{collectionId}/items",
                    rel: RelationTypes.Self,
                    type: MediaTypes.GeoJson,
                    title: "Items"),
                Link.Create(
                    href: $"{stacBase}/collections/{collectionId}",
                    rel: RelationTypes.Collection,
                    type: MediaTypes.Json,
                    title: layer.Name),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"));

            var response = new StacItemCollection
            {
                Features = items,
                Links = links,
                NumberReturned = items.Length,
                NumberMatched = result.TotalCount,
                Context = new StacSearchContext
                {
                    Returned = items.Length,
                    Matched = result.TotalCount,
                    Limit = effectiveLimit
                }
            };

            StacLog.ItemsReturned(logger, items.Length, collectionId);
            return Results.Json(response, StacJsonContext.Default.StacItemCollection, MediaTypes.GeoJson);
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
                context, "An error occurred while retrieving STAC items.");
        }
    }

    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string itemId,
        HttpContext context,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        StacLog.ItemRequested(logger, collectionId, itemId);

        try
        {
            if (!int.TryParse(collectionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId) ||
                !long.TryParse(itemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Item '{itemId}' not found.");
            }

            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var layer = await layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer is null)
            {
                StacLog.CollectionNotFound(logger, collectionId);
                return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' not found.");
            }

            var feature = await featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (feature is null)
            {
                StacLog.ItemNotFound(logger, collectionId, itemId);
                return StandardErrorHelpers.CreateNotFound(context, $"Item '{itemId}' not found in collection '{collectionId}'.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var item = StacMappingService.MapFeatureToItem(feature.Value, layer, baseUrl);

            return Results.Json(item, StacJsonContext.Default.StacItem, MediaTypes.GeoJson);
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
                context, "An error occurred while retrieving the STAC item.");
        }
    }
}

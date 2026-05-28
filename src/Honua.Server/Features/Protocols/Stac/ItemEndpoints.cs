// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Stac.Models;
using Honua.Server.Features.Protocols.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Stac;

/// <summary>
/// STAC item listing and detail endpoints for a collection.
/// </summary>
internal static class ItemEndpoints
{
    private const int Wgs84Srid = 4326;

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
        [FromQuery] int? offset,
        [FromQuery] string? bbox,
        [FromQuery] string? datetime,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Items,
            "/stac/collections/{collectionId}/items",
            HttpMethods.Get,
            collectionId);
        StacLog.ItemsRequested(logger, collectionId, limit);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.Items);
        if (validationError is not null)
        {
            StacTelemetry.SetFailed(activity, "invalid_query_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context, collectionId, requiredProtocol: StacProtocolConstants.ProtocolName, cancellationToken: cancellationToken);
            if (!validation.IsValid)
            {
                StacTelemetry.SetFailed(activity, "collection_not_found_or_forbidden");
                StacLog.CollectionNotFound(logger, collectionId);
                return validation.ErrorResult!;
            }

            var resource = validation.Resource!;
            var publication = validation.Publication!;
            var layerId = publication.LayerIndex
                ?? throw new InvalidOperationException(
                    $"Publication {publication.Metadata.Id} has no LayerIndex; the STAC items handler requires one.");
            var resourceSrid = resource.ReadSrid() ?? Wgs84Srid;

            var effectiveLimit = Math.Clamp(limit ?? StacConstants.DefaultSearchLimit, 1, StacConstants.MaxSearchLimit);
            var effectiveOffset = Math.Max(offset ?? 0, 0);

            var query = new FeatureQuery
            {
                Limit = effectiveLimit,
                Offset = effectiveOffset > 0 ? effectiveOffset : null,
                SpatialReferenceSrid = resourceSrid,
                OutputSrid = Wgs84Srid
            };

            if (!string.IsNullOrWhiteSpace(bbox))
            {
                var spatialFilter = StacFilterHelpers.ParseBbox(bbox);
                if (spatialFilter is null)
                {
                    StacTelemetry.SetFailed(activity, "invalid_bbox");
                    return StandardErrorHelpers.CreateBadRequest(context, "Invalid bbox parameter.");
                }
                query = query with { SpatialFilter = spatialFilter };
            }

            if (!string.IsNullOrWhiteSpace(datetime))
            {
                var temporalFilter = StacFilterHelpers.ParseDatetime(datetime, resource);
                if (temporalFilter is null)
                {
                    StacTelemetry.SetFailed(activity, "invalid_datetime");
                    return StandardErrorHelpers.CreateBadRequest(context, "Invalid datetime parameter.");
                }
                query = query with { TemporalFilter = temporalFilter };
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var result = await featureReader.QueryAsync(layerId, query, cancellationToken);

            var items = result.Features
                .Select(f => StacMappingService.MapFeatureToItem(f, resource, publication, layerId, baseUrl, geometrySrid: Wgs84Srid))
                .ToImmutableArray();

            var stacBase = $"{baseUrl}/stac";
            var itemsPath = $"{stacBase}/collections/{collectionId}/items";
            var requestedItemsUrl = $"{baseUrl}{context.Request.Path}{context.Request.QueryString}";
            var collectionTitle = resource.Metadata.Title ?? resource.Metadata.Name;
            var linksBuilder = ImmutableArray.CreateBuilder<Link>();
            linksBuilder.Add(Link.Create(
                href: requestedItemsUrl,
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: "Items"));
            linksBuilder.Add(Link.Create(
                href: $"{stacBase}/collections/{collectionId}",
                rel: RelationTypes.Collection,
                type: MediaTypes.Json,
                title: collectionTitle));
            linksBuilder.Add(Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

            var nextOffset = effectiveOffset + items.Length;
            if (result.TotalCount > nextOffset)
            {
                linksBuilder.Add(Link.Create(
                    href: $"{itemsPath}?{BuildItemsFilterQuery(effectiveLimit, nextOffset, bbox, datetime)}",
                    rel: "next",
                    type: MediaTypes.GeoJson,
                    title: "Next page"));
            }

            var response = new StacItemCollection
            {
                Features = items,
                Links = linksBuilder.ToImmutable(),
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
            StacTelemetry.SetResultCount(activity, items.Length, result.TotalCount);
            return Results.Json(response, StacJsonContext.Default.StacItemCollection, MediaTypes.GeoJson);
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
                context, "An error occurred while retrieving STAC items.");
        }
    }

    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string itemId,
        HttpContext context,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.Item,
            "/stac/collections/{collectionId}/items/{itemId}",
            HttpMethods.Get,
            collectionId,
            itemId);
        StacLog.ItemRequested(logger, collectionId, itemId);

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var validation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
                context, collectionId, requiredProtocol: StacProtocolConstants.ProtocolName, cancellationToken: cancellationToken);
            if (!validation.IsValid)
            {
                StacTelemetry.SetFailed(activity, "collection_not_found_or_forbidden");
                StacLog.CollectionNotFound(logger, collectionId);
                return validation.ErrorResult!;
            }

            var resource = validation.Resource!;
            var publication = validation.Publication!;
            var layerId = publication.LayerIndex
                ?? throw new InvalidOperationException(
                    $"Publication {publication.Metadata.Id} has no LayerIndex; the STAC item handler requires one.");
            var resourceSrid = resource.ReadSrid() ?? Wgs84Srid;

            Feature? feature = await TryGetFeatureByCanonicalItemIdAsync(
                featureReader,
                layerId,
                resourceSrid,
                itemId,
                cancellationToken);
            if (feature is null && long.TryParse(itemId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                feature = await TryGetFeatureByObjectIdAsStacAsync(
                    featureReader,
                    layerId,
                    resourceSrid,
                    objectId,
                    cancellationToken);
            }

            if (feature is null)
            {
                StacTelemetry.SetFailed(activity, "item_not_found");
                StacLog.ItemNotFound(logger, collectionId, itemId);
                return StandardErrorHelpers.CreateNotFound(context, $"Item '{itemId}' not found in collection '{collectionId}'.");
            }

            var baseUrl = BaseUrlResolver.GetBaseUrl(context);
            var item = StacMappingService.MapFeatureToItem(feature.Value, resource, publication, layerId, baseUrl, geometrySrid: Wgs84Srid);

            StacTelemetry.SetResultCount(activity, 1);
            return Results.Json(item, StacJsonContext.Default.StacItem, MediaTypes.GeoJson);
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
                context, "An error occurred while retrieving the STAC item.");
        }
    }

    private static string BuildItemsFilterQuery(int limit, int offset, string? bbox, string? datetime)
    {
        var query = $"limit={limit}&offset={offset}";
        if (!string.IsNullOrWhiteSpace(bbox))
            query += $"&bbox={Uri.EscapeDataString(bbox)}";
        if (!string.IsNullOrWhiteSpace(datetime))
            query += $"&datetime={Uri.EscapeDataString(datetime)}";
        return query;
    }

    private static async Task<Feature?> TryGetFeatureByCanonicalItemIdAsync(
        IFeatureReader featureReader,
        int layerId,
        int layerSrid,
        string itemId,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            SqlFilter = new SqlFragment(
                "attributes->>'stac_id' = @p0 OR attributes->>'item_id' = @p0 OR attributes->>'id' = @p0",
                [itemId])
        };

        var objectIds = await featureReader.QueryObjectIdsAsync(layerId, query, cancellationToken);
        Feature? bestMatch = null;
        var bestRank = int.MaxValue;

        foreach (var objectId in objectIds)
        {
            var feature = await TryGetFeatureByObjectIdAsStacAsync(
                featureReader,
                layerId,
                layerSrid,
                objectId,
                cancellationToken);
            var matchRank = GetCanonicalItemMatchRank(feature, itemId);
            if (!matchRank.HasValue)
            {
                continue;
            }

            if (matchRank.Value < bestRank)
            {
                bestMatch = feature;
                bestRank = matchRank.Value;

                if (bestRank == 0)
                {
                    break;
                }
            }
        }

        return bestMatch;
    }

    private static async Task<Feature?> TryGetFeatureByObjectIdAsStacAsync(
        IFeatureReader featureReader,
        int layerId,
        int layerSrid,
        long objectId,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            ObjectIds = ImmutableArray.Create(objectId),
            SpatialReferenceSrid = layerSrid,
            OutputSrid = Wgs84Srid,
            Limit = 1
        };

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken);
        foreach (var feature in result.Features)
        {
            return feature;
        }

        return null;
    }

    private static int? GetCanonicalItemMatchRank(Feature? feature, string itemId)
    {
        var attributes = feature?.Attributes;
        if (attributes is null)
        {
            return null;
        }

        var keys = ImmutableArray.Create("stac_id", "item_id", "id");
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            if (!attributes.TryGetValue(key, out var value) || value is null)
            {
                continue;
            }

            if (string.Equals(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    itemId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return null;
    }
}

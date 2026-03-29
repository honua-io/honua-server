// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.Stac.Models;
using Honua.Server.Features.Stac.Services;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Stac;

/// <summary>
/// STAC Item Search endpoint (GET and POST).
/// </summary>
internal static class SearchEndpoints
{
    /// <summary>
    /// Maps the STAC search endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapStacSearchEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/stac/search", HandleSearchGet)
            .WithDisplayName("STAC Search (GET)")
            .WithName("StacSearchGet")
            .WithSummary("Search STAC items via GET")
            .WithDescription("Searches STAC items across collections with spatial, temporal, and property filters")
            .WithTags("STAC")
            .Produces<StacItemCollection>(200, MediaTypes.GeoJson)
            .Produces(400);

        endpoints.MapPost("/stac/search", HandleSearchPost)
            .WithDisplayName("STAC Search (POST)")
            .WithName("StacSearchPost")
            .WithSummary("Search STAC items via POST")
            .WithDescription("Searches STAC items across collections with a JSON request body")
            .WithTags("STAC")
            .Accepts<StacSearchRequest>(MediaTypes.Json)
            .Produces<StacItemCollection>(200, MediaTypes.GeoJson)
            .Produces(400);

        return endpoints;
    }

    private static async Task<IResult> HandleSearchGet(
        HttpContext context,
        [FromQuery] int? limit,
        [FromQuery] string? bbox,
        [FromQuery] string? datetime,
        [FromQuery] string? collections,
        [FromQuery] string? ids,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.SearchGet);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        // Convert GET parameters to a search request
        var request = new StacSearchRequest
        {
            Limit = limit,
            Datetime = datetime
        };

        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',');
            if (parts.Length >= 4)
            {
                var bboxValues = new List<double>();
                foreach (var part in parts)
                {
                    if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                    {
                        bboxValues.Add(val);
                    }
                }

                if (bboxValues.Count >= 4)
                {
                    request = request with { Bbox = bboxValues.ToImmutableArray() };
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(collections))
        {
            request = request with { Collections = collections.Split(',').ToImmutableArray() };
        }

        if (!string.IsNullOrWhiteSpace(ids))
        {
            request = request with { Ids = ids.Split(',').ToImmutableArray() };
        }

        return await ExecuteSearchAsync(request, context, layerCatalog, featureReader, logger);
    }

    private static async Task<IResult> HandleSearchPost(
        HttpContext context,
        [FromBody] StacSearchRequest request,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        return await ExecuteSearchAsync(request, context, layerCatalog, featureReader, logger);
    }

    private static async Task<IResult> ExecuteSearchAsync(
        StacSearchRequest request,
        HttpContext context,
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        ILogger logger)
    {
        var effectiveLimit = Math.Clamp(
            request.Limit ?? StacConstants.DefaultSearchLimit,
            1,
            StacConstants.MaxSearchLimit);

        var collectionCount = request.Collections is { IsDefault: false } c ? c.Length : 0;
        StacLog.SearchRequested(logger, collectionCount, effectiveLimit);

        try
        {
            var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
            var baseUrl = BaseUrlResolver.GetBaseUrl(context);

            // Resolve target layers
            var allLayers = await layerCatalog.ListLayersAsync(cancellationToken);
            var services = await layerCatalog.ListServicesAsync(cancellationToken);
            var layerToService = new Dictionary<int, ServiceDefinition>();
            foreach (var service in services)
            {
                foreach (var serviceLayer in service.Layers)
                {
                    layerToService.TryAdd(serviceLayer.Id, service);
                }
            }

            var targetLayers = allLayers
                .Where(layer => AccessPolicyHelpers.IsLayerAccessible(
                    context, layer, layerToService.GetValueOrDefault(layer.Id)));

            // Filter by collection IDs if specified
            if (request.Collections is { IsDefault: false } requestedCollections && requestedCollections.Length > 0)
            {
                var collectionIds = requestedCollections
                    .Where(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .Select(id => int.Parse(id, CultureInfo.InvariantCulture))
                    .ToHashSet();
                targetLayers = targetLayers.Where(l => collectionIds.Contains(l.Id));
            }

            var layerList = targetLayers.ToArray();
            var allItems = ImmutableArray.CreateBuilder<StacItem>();
            long totalMatched = 0;

            // Query each target layer
            foreach (var layer in layerList)
            {
                if (allItems.Count >= effectiveLimit)
                {
                    break;
                }

                var remaining = effectiveLimit - allItems.Count;
                var query = new FeatureQuery
                {
                    Limit = remaining
                };

                // Apply bbox filter
                if (request.Bbox is { IsDefault: false } bboxArr && bboxArr.Length >= 4)
                {
                    var bboxStr = string.Join(",", bboxArr.Select(v => v.ToString(CultureInfo.InvariantCulture)));
                    var spatialFilter = StacFilterHelpers.ParseBbox(bboxStr);
                    if (spatialFilter is not null)
                    {
                        query = query with { SpatialFilter = spatialFilter };
                    }
                }

                // Apply datetime filter
                if (!string.IsNullOrWhiteSpace(request.Datetime))
                {
                    var temporalFilter = StacFilterHelpers.ParseDatetime(request.Datetime, layer);
                    if (temporalFilter is not null)
                    {
                        query = query with { TemporalFilter = temporalFilter };
                    }
                }

                // Apply ID filter
                if (request.Ids is { IsDefault: false } requestedIds && requestedIds.Length > 0)
                {
                    var objectIds = requestedIds
                        .Where(id => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        .Select(id => long.Parse(id, CultureInfo.InvariantCulture))
                        .ToImmutableArray();
                    if (objectIds.Length > 0)
                    {
                        query = query with { ObjectIds = objectIds };
                    }
                }

                // Apply sort
                if (request.Sortby is { IsDefault: false } sortby && sortby.Length > 0)
                {
                    var orderBy = sortby.Select(s =>
                        string.Equals(s.Direction, "desc", StringComparison.OrdinalIgnoreCase)
                            ? OrderByClause.Desc(s.Field)
                            : OrderByClause.Asc(s.Field))
                        .ToImmutableArray();
                    query = query with { OrderBy = orderBy };
                }

                // Apply field selection
                if (request.Fields is { Includes: { IsDefault: false } includes } && includes.Length > 0)
                {
                    query = query with { OutFields = includes };
                }

                var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                totalMatched += result.TotalCount;

                var items = result.Features
                    .Select(f => StacMappingService.MapFeatureToItem(f, layer, baseUrl));
                allItems.AddRange(items);
            }

            var stacBase = $"{baseUrl}/stac";
            var links = ImmutableArray.Create(
                Link.Create(
                    href: $"{stacBase}/search",
                    rel: RelationTypes.Self,
                    type: MediaTypes.GeoJson,
                    title: "Search results"),
                Link.Create(
                    href: stacBase,
                    rel: StacConstants.StacRelations.Root,
                    type: MediaTypes.Json,
                    title: "STAC Catalog"));

            var response = new StacItemCollection
            {
                Features = allItems.ToImmutable(),
                Links = links,
                NumberReturned = allItems.Count,
                NumberMatched = totalMatched,
                Context = new StacSearchContext
                {
                    Returned = allItems.Count,
                    Matched = totalMatched,
                    Limit = effectiveLimit
                }
            };

            StacLog.SearchReturned(logger, allItems.Count);
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
                context, "An error occurred during STAC search.");
        }
    }
}

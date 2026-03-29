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
        [FromQuery] int? offset,
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
        var effectiveOffset = Math.Max(offset ?? 0, 0);

        if (!string.IsNullOrWhiteSpace(bbox))
        {
            var parts = bbox.Split(',');
            if (parts.Length is 4 or 6)
            {
                var bboxValues = new double[parts.Length];
                var allValid = true;
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out bboxValues[i]))
                    {
                        allValid = false;
                        break;
                    }
                }

                if (allValid)
                {
                    request = request with { Bbox = ImmutableArray.Create(bboxValues) };
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

        return await ExecuteSearchAsync(request, effectiveOffset, context, layerCatalog, featureReader, logger);
    }

    private static async Task<IResult> HandleSearchPost(
        HttpContext context,
        [FromBody] StacSearchRequest request,
        [FromServices] ILayerCatalog layerCatalog,
        [FromServices] IFeatureReader featureReader,
        [FromServices] ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        return await ExecuteSearchAsync(request, 0, context, layerCatalog, featureReader, logger);
    }

    private static async Task<IResult> ExecuteSearchAsync(
        StacSearchRequest request,
        int offset,
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

            // Resolve target layers with STAC protocol gating + access policy
            var visibleLayers = await StacFilterHelpers.ResolveStacVisibleLayersAsync(
                context, layerCatalog, cancellationToken);

            IEnumerable<Core.Features.Catalog.Domain.LayerDefinition> targetLayers = visibleLayers;

            // Filter by collection IDs if specified
            if (request.Collections is { IsDefault: false } requestedCollections && requestedCollections.Length > 0)
            {
                var collectionIds = requestedCollections
                    .Where(id => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .Select(id => int.Parse(id, CultureInfo.InvariantCulture))
                    .ToHashSet();
                targetLayers = targetLayers.Where(l => collectionIds.Contains(l.Id));
            }

            // Pre-parse IDs filter; if provided but none are valid, return empty results
            ImmutableArray<long>? parsedObjectIds = null;
            if (request.Ids is { IsDefault: false } requestedIds && requestedIds.Length > 0)
            {
                var objectIds = requestedIds
                    .Where(id => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .Select(id => long.Parse(id, CultureInfo.InvariantCulture))
                    .ToImmutableArray();
                if (objectIds.Length == 0)
                {
                    // IDs were provided but none could be parsed — zero results
                    parsedObjectIds = ImmutableArray<long>.Empty;
                }
                else
                {
                    parsedObjectIds = objectIds;
                }
            }

            var layerList = targetLayers.ToArray();
            var allItems = ImmutableArray.CreateBuilder<StacItem>();
            long totalMatched = 0;

            // Short-circuit: if IDs filter resolved to empty, skip all queries
            var hasEmptyIdFilter = parsedObjectIds is { Length: 0 };
            var remainingSkip = offset;

            // Query each target layer — continue for all layers to accumulate totalMatched
            foreach (var layer in layerList)
            {
                if (hasEmptyIdFilter)
                {
                    break;
                }

                var query = BuildLayerQuery(request, layer, parsedObjectIds);

                if (remainingSkip > 0)
                {
                    // Count this layer to decide how much to skip
                    var layerCount = await featureReader.CountAsync(layer.Id, query, cancellationToken);
                    totalMatched += layerCount;

                    if (remainingSkip >= layerCount)
                    {
                        remainingSkip -= (int)Math.Min(layerCount, int.MaxValue);
                        continue;
                    }

                    // Partial skip: fetch from offset within this layer
                    var remaining = effectiveLimit - allItems.Count;
                    query = query with { Offset = remainingSkip, Limit = remaining };
                    remainingSkip = 0;

                    var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                    allItems.AddRange(result.Features
                        .Select(f => StacMappingService.MapFeatureToItem(f, layer, baseUrl)));
                }
                else if (allItems.Count < effectiveLimit)
                {
                    var remaining = effectiveLimit - allItems.Count;
                    query = query with { Limit = remaining };

                    var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                    totalMatched += result.TotalCount;

                    allItems.AddRange(result.Features
                        .Select(f => StacMappingService.MapFeatureToItem(f, layer, baseUrl)));
                }
                else
                {
                    // Page is full — count only to get accurate totalMatched
                    totalMatched += await featureReader.CountAsync(layer.Id, query, cancellationToken);
                }
            }

            var stacBase = $"{baseUrl}/stac";
            var linksBuilder = ImmutableArray.CreateBuilder<Link>();
            linksBuilder.Add(Link.Create(
                href: $"{stacBase}/search?{BuildSearchQuery(effectiveLimit, offset, request)}",
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: "Search results"));
            linksBuilder.Add(Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

            // Add next link when more items exist beyond the current page
            var nextOffset = offset + allItems.Count;
            if (totalMatched > nextOffset)
            {
                linksBuilder.Add(Link.Create(
                    href: $"{stacBase}/search?{BuildSearchQuery(effectiveLimit, nextOffset, request)}",
                    rel: "next",
                    type: MediaTypes.GeoJson,
                    title: "Next page"));
            }

            var response = new StacItemCollection
            {
                Features = allItems.ToImmutable(),
                Links = linksBuilder.ToImmutable(),
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

    private static FeatureQuery BuildLayerQuery(
        StacSearchRequest request,
        Core.Features.Catalog.Domain.LayerDefinition layer,
        ImmutableArray<long>? parsedObjectIds)
    {
        var query = new FeatureQuery();

        // Apply bbox filter
        if (request.Bbox is { IsDefault: false } bboxArr && bboxArr.Length >= 4)
        {
            var spatialFilter = StacFilterHelpers.CreateBboxSpatialFilter(
                west: bboxArr[0], south: bboxArr[1], east: bboxArr[2], north: bboxArr[3]);
            query = query with { SpatialFilter = spatialFilter };
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
        if (parsedObjectIds is { Length: > 0 } objectIds)
        {
            query = query with { ObjectIds = objectIds };
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

        return query;
    }

    private static string BuildSearchQuery(int limit, int offset, StacSearchRequest request)
    {
        var query = $"limit={limit}&offset={offset}";
        if (request.Bbox is { IsDefault: false } bboxArr && bboxArr.Length >= 4)
            query += "&bbox=" + string.Join(",", bboxArr.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(request.Datetime))
            query += $"&datetime={request.Datetime}";
        if (request.Collections is { IsDefault: false } cols && cols.Length > 0)
            query += $"&collections={string.Join(",", cols)}";
        if (request.Ids is { IsDefault: false } ids && ids.Length > 0)
            query += $"&ids={string.Join(",", ids)}";
        return query;
    }
}

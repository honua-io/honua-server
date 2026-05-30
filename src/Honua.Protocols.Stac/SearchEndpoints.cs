// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Filtering;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Stac.Models;
using Honua.Protocols.Stac.Services;
using Honua.Core.Features.Geometry.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Protocols.Stac;

/// <summary>
/// STAC Item Search endpoint (GET and POST).
/// </summary>
internal static class SearchEndpoints
{
    private const int Wgs84Srid = 4326;
    private delegate bool TryParseDelegate<TValue>(string input, out TValue value);

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

        // Read-only OGC/STAC search surface; anonymous by design. The POST form
        // mirrors the GET search semantics — the body just carries the same
        // filter parameters as a JSON document — and is gated downstream by the
        // per-publication access policy via StacV2Lookups.ResolveVisibleStacPublicationsAsync.
        endpoints.MapPost("/stac/search", HandleSearchPost)
            .WithDisplayName("STAC Search (POST)")
            .WithName("StacSearchPost")
            .WithSummary("Search STAC items via POST")
            .WithDescription("Searches STAC items across collections with a JSON request body")
            .WithTags("STAC")
            .Accepts<StacSearchRequest>(MediaTypes.Json)
            .AllowAnonymous()
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
        [FromQuery] string? intersects,
        [FromQuery] string? fields,
        [FromQuery] string? sortby,
        [FromQuery(Name = "filter")] string? filter,
        [FromQuery(Name = "filter-lang")] string? filterLang,
        [FromQuery(Name = "filter-crs")] string? filterCrs,
        [FromServices] StacSearchDependencies deps)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.SearchGet,
            "/stac/search",
            HttpMethods.Get);

        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.SearchGet);
        if (validationError is not null)
        {
            StacTelemetry.SetFailed(activity, "invalid_query_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!TryBuildSearchRequestFromGet(
                limit,
                datetime,
                bbox,
                collections,
                ids,
                intersects,
                fields,
                sortby,
                filter,
                filterLang,
                filterCrs,
                true,
                out var request,
                out var requestError))
        {
            StacTelemetry.SetFailed(activity, "invalid_search_parameters");
            return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid search parameters.");
        }

        var effectiveOffset = Math.Max(offset ?? 0, 0);

        return await ExecuteSearchAsync(
            request,
            effectiveOffset,
            context,
            deps.FeatureReader,
            deps.GeometryService,
            deps.FilterProcessor,
            defaultFilterLangIsText: true,
            deps.Logger);
    }

    private static async Task<IResult> HandleSearchPost(
        HttpContext context,
        [FromBody] StacSearchRequest request,
        [FromServices] StacSearchDependencies deps)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.SearchPost,
            "/stac/search",
            HttpMethods.Post);

        return await ExecuteSearchAsync(
            request,
            0,
            context,
            deps.FeatureReader,
            deps.GeometryService,
            deps.FilterProcessor,
            defaultFilterLangIsText: false,
            deps.Logger);
    }

    private static async Task<IResult> ExecuteSearchAsync(
        StacSearchRequest request,
        int offset,
        HttpContext context,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        ILogger logger)
    {
        // TODO(#1144): wire tenant filter — resolve ITenantContext from context.RequestServices
        // and constrain V2 graph lookups + featureReader queries to the caller's tenant id.
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.SearchExecute,
            "/stac/search",
            context.Request.Method);

        if (request.Limit is < 1)
        {
            StacTelemetry.SetFailed(activity, "invalid_limit");
            return StandardErrorHelpers.CreateBadRequest(context, "limit must be greater than or equal to 1.");
        }

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

            // Resolve target publications with STAC protocol gating + access policy
            // off the V2 metadata graph.
            var visible = await StacV2Lookups.ResolveVisibleStacPublicationsAsync(
                context, cancellationToken).ConfigureAwait(false);

            // Multiple STAC-enabled publications can resolve to the same storage layer
            // (e.g. one resource published through two services). The v1 layer-keyed
            // walk produced a single hit per layer; preserve that contract so feature
            // counts and result-sets remain stable. Keep the first encountered
            // publication — ResolveVisibleStacPublicationsAsync already orders by
            // LayerIndex so this is deterministic.
            IEnumerable<StacV2Lookups.ResolvedStacPublication> targets = visible
                .GroupBy(t => t.LayerIndex)
                .Select(g => g.First())
                .ToArray();

            if (request.Collections is { IsDefault: false } requestedCollections && requestedCollections.Length > 0)
            {
                if (!TryParseIntegerTokenSet(requestedCollections, out var collectionIds, out var collectionError))
                {
                    StacTelemetry.SetFailed(activity, "invalid_collections");
                    return StandardErrorHelpers.CreateBadRequest(context, collectionError ?? "Invalid collections parameter.");
                }

                targets = targets.Where(t => collectionIds.Contains(t.LayerIndex));
            }

            if (request.Ids is { IsDefault: false } requestedIds && requestedIds.Length > 0)
            {
                if (!TryNormalizeIdTokenSet(requestedIds, out var normalizedIds, out var idsError))
                {
                    StacTelemetry.SetFailed(activity, "invalid_ids");
                    return StandardErrorHelpers.CreateBadRequest(context, idsError ?? "Invalid ids parameter.");
                }

                var targetList = targets.ToArray();
                StacTelemetry.SetSearchTags(activity, effectiveLimit, offset, collectionCount, targetList.Length);
                return await ExecuteSearchAcrossPublicationsAsync(
                    request,
                    offset,
                    context,
                    baseUrl,
                    featureReader,
                    geometryService,
                    filterProcessor,
                    defaultFilterLangIsText,
                    logger,
                    targetList,
                    normalizedIds,
                    effectiveLimit);
            }

            var allTargets = targets.ToArray();
            StacTelemetry.SetSearchTags(activity, effectiveLimit, offset, collectionCount, allTargets.Length);
            return await ExecuteSearchAcrossPublicationsAsync(
                request,
                offset,
                context,
                baseUrl,
                featureReader,
                geometryService,
                filterProcessor,
                defaultFilterLangIsText,
                logger,
                allTargets,
                null,
                effectiveLimit);
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
                context, "An error occurred during STAC search.");
        }
    }

    private static async Task<IResult> ExecuteSearchAcrossPublicationsAsync(
        StacSearchRequest request,
        int offset,
        HttpContext context,
        string baseUrl,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        ILogger logger,
        StacV2Lookups.ResolvedStacPublication[] targets,
        ImmutableArray<string>? requestedItemIds,
        int effectiveLimit)
    {
        using var activity = StacTelemetry.StartActivity(
            StacTelemetry.Operations.SearchWork,
            "/stac/search",
            context.Request.Method);
        StacTelemetry.SetSearchTags(
            activity,
            effectiveLimit,
            offset,
            request.Collections is { IsDefault: false } collections ? collections.Length : 0,
            targets.Length);

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        try
        {
            var allItems = ImmutableArray.CreateBuilder<StacItem>();
            long totalMatched = 0;
            var remainingSkip = offset;
            var hasEmptyIdFilter = requestedItemIds is { Length: 0 };
            foreach (var target in targets)
            {
                if (hasEmptyIdFilter)
                {
                    break;
                }

                var layerQueryResult = await TryBuildLayerQuery(
                    request,
                    target.Resource,
                    requestedItemIds,
                    geometryService,
                    filterProcessor,
                    defaultFilterLangIsText,
                    cancellationToken);
                if (!layerQueryResult.IsSuccess)
                {
                    StacTelemetry.SetFailed(activity, "invalid_search_parameters");
                    return StandardErrorHelpers.CreateBadRequest(context, layerQueryResult.Error ?? "Invalid search parameters.");
                }

                var query = layerQueryResult.Query;
                var projection = layerQueryResult.Projection;
                var layerId = target.LayerIndex;

                if (remainingSkip > 0)
                {
                    var layerCount = await featureReader.CountAsync(layerId, query, cancellationToken);
                    totalMatched += layerCount;

                    if (remainingSkip >= layerCount)
                    {
                        remainingSkip -= (int)Math.Min(layerCount, int.MaxValue);
                        continue;
                    }

                    var remaining = effectiveLimit - allItems.Count;
                    query = query with { Offset = remainingSkip, Limit = remaining };
                    remainingSkip = 0;

                    var result = await featureReader.QueryAsync(layerId, query, cancellationToken);
                    allItems.AddRange(result.Features
                        .Select(f => ApplyFieldProjection(
                            StacMappingService.MapFeatureToItem(
                                f,
                                target.Resource,
                                target.Publication,
                                layerId,
                                baseUrl,
                                projection?.SelectedProperties,
                                geometrySrid: Wgs84Srid),
                            projection)));
                }
                else if (allItems.Count < effectiveLimit)
                {
                    var remaining = effectiveLimit - allItems.Count;
                    query = query with { Limit = remaining };

                    var result = await featureReader.QueryAsync(layerId, query, cancellationToken);
                    totalMatched += result.TotalCount;

                    allItems.AddRange(result.Features
                        .Select(f => ApplyFieldProjection(
                            StacMappingService.MapFeatureToItem(
                                f,
                                target.Resource,
                                target.Publication,
                                layerId,
                                baseUrl,
                                projection?.SelectedProperties,
                                geometrySrid: Wgs84Srid),
                            projection)));
                }
                else
                {
                    totalMatched += await featureReader.CountAsync(layerId, query, cancellationToken);
                }
            }

            var stacBase = $"{baseUrl}/stac";
            var linksBuilder = ImmutableArray.CreateBuilder<Link>();
            linksBuilder.Add(Link.Create(
                href: $"{stacBase}/search?{BuildSearchQuery(effectiveLimit, offset, request, defaultFilterLangIsText)}",
                rel: RelationTypes.Self,
                type: MediaTypes.GeoJson,
                title: "Search results"));
            linksBuilder.Add(Link.Create(
                href: stacBase,
                rel: StacConstants.StacRelations.Root,
                type: MediaTypes.Json,
                title: "STAC Catalog"));

            var nextOffset = offset + allItems.Count;
            if (totalMatched > nextOffset)
            {
                linksBuilder.Add(Link.Create(
                    href: $"{stacBase}/search?{BuildSearchQuery(effectiveLimit, nextOffset, request, defaultFilterLangIsText)}",
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
            StacTelemetry.SetResultCount(activity, allItems.Count, totalMatched);
            return Results.Json(response, StacJsonContext.Default.StacItemCollection, MediaTypes.GeoJson);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
        catch (Exception ex)
        {
            StacTelemetry.RecordException(activity, ex);
            throw;
        }
    }

    private static async Task<(bool IsSuccess, FeatureQuery Query, StacFieldProjection? Projection, string? Error)> TryBuildLayerQuery(
        StacSearchRequest request,
        MetadataV2Resource resource,
        ImmutableArray<string>? requestedItemIds,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken)
    {
        var resourceSrid = resource.ReadSrid() ?? Wgs84Srid;
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = resourceSrid,
            OutputSrid = Wgs84Srid
        };
        StacFieldProjection? projection = null;
        string? error = null;

        if (request.Bbox is { IsDefault: false } bboxArr)
        {
            if (!StacFilterHelpers.TryExtractBbox2D(
                    bboxArr,
                    out var west,
                    out var south,
                    out var east,
                    out var north,
                    out error))
            {
                return (false, query, projection, error);
            }

            if (request.Intersects.HasValue)
            {
                error = "bbox and intersects cannot be combined.";
                return (false, query, projection, error);
            }

            query = query with
            {
                SpatialFilter = StacFilterHelpers.CreateBboxSpatialFilter(
                    west: west, south: south, east: east, north: north)
            };
        }

        if (request.Intersects.HasValue)
        {
            if (!StacFilterHelpers.TryCreateIntersectsSpatialFilter(
                    request.Intersects.Value.GetRawText(),
                    geometryService,
                    out var intersectsSpatialFilter,
                    out var intersectsError))
            {
                error = intersectsError;
                return (false, query, projection, error);
            }

            if (intersectsSpatialFilter.HasValue)
            {
                query = query with { SpatialFilter = intersectsSpatialFilter };
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Datetime))
        {
            var temporalFilter = StacFilterHelpers.ParseDatetime(request.Datetime, resource);
            if (temporalFilter is null)
            {
                error = "Invalid datetime parameter.";
                return (false, query, projection, error);
            }

            query = query with { TemporalFilter = temporalFilter };
        }

        var filterQueryResult = await TryResolveFilterQuery(
            request,
            resource,
            filterProcessor,
            defaultFilterLangIsText,
            cancellationToken);
        if (!filterQueryResult.IsSuccess)
        {
            error = filterQueryResult.Error;
            return (false, query, projection, error);
        }

        if (filterQueryResult.SqlFilter is not null)
        {
            query = query with { SqlFilter = filterQueryResult.SqlFilter };
        }

        if (requestedItemIds is { Length: > 0 } itemIds)
        {
            query = query with { SqlFilter = CombineSqlFilters(query.SqlFilter, BuildItemIdsSqlFilter(itemIds)) };
        }

        if (request.Sortby is { IsDefault: false } sortby && sortby.Length > 0)
        {
            if (!TryBuildSortOrder(resource, sortby, out var orderBy, out var sortError))
            {
                error = sortError;
                return (false, query, projection, error);
            }

            query = query with { OrderBy = orderBy };
        }

        if (request.Fields is not null)
        {
            if (!TryBuildFieldSelection(resource, request.Fields, out var outFields, out projection, out var fieldError))
            {
                error = fieldError;
                return (false, query, projection, error);
            }

            query = query with { OutFields = outFields };
        }

        return (true, query, projection, null);
    }

    private static async Task<(bool IsSuccess, SqlFragment? SqlFilter, string? Error)> TryResolveFilterQuery(
        StacSearchRequest request,
        MetadataV2Resource resource,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken)
    {
        var result = await filterProcessor.ProcessFilterAsync(
            resource,
            request.Filter,
            request.FilterLang,
            request.FilterCrs,
            defaultFilterLangIsText,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? (true, result.SqlFilter, null)
            : (false, null, result.ErrorMessage);
    }

    private static bool TryBuildSortOrder(
        MetadataV2Resource resource,
        ImmutableArray<StacSortDefinition> sortby,
        out ImmutableArray<OrderByClause> orderBy,
        out string? error)
    {
        var schemaFields = resource.SchemaFields ?? Array.Empty<MetadataV2Field>();
        var availableFields = schemaFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
        var orderByBuilder = ImmutableArray.CreateBuilder<OrderByClause>(sortby.Length);

        foreach (var sort in sortby)
        {
            if (string.IsNullOrWhiteSpace(sort.Field))
            {
                error = "sortby contains an empty field name.";
                orderBy = default;
                return false;
            }

            if (!TryNormalizePropertyName(sort.Field, out var normalizedField))
            {
                error = $"Invalid sort field '{sort.Field}'.";
                orderBy = default;
                return false;
            }

            if (!availableFields.ContainsKey(normalizedField))
            {
                error = $"Unknown sort field '{sort.Field}'.";
                orderBy = default;
                return false;
            }

            if (string.Equals(sort.Direction, "desc", StringComparison.OrdinalIgnoreCase))
            {
                orderByBuilder.Add(OrderByClause.Desc(normalizedField));
                continue;
            }

            if (string.Equals(sort.Direction, "asc", StringComparison.OrdinalIgnoreCase))
            {
                orderByBuilder.Add(OrderByClause.Asc(normalizedField));
                continue;
            }

            error = $"Invalid sort direction '{sort.Direction}'. Use 'asc' or 'desc'.";
            orderBy = default;
            return false;
        }

        error = null;
        orderBy = orderByBuilder.ToImmutable();
        return true;
    }

    // TODO(#1035 follow-up): the V2 fields lookup is now keyed on
    // <see cref="MetadataV2Resource.SchemaFields"/>, so query parameters like
    // <c>fields=+properties.foo</c> only resolve when the V2 graph carries <c>foo</c>
    // as a schema field. Tests that mutate the v1 <c>honua.layer_fields</c> table
    // (e.g. WebAppFixture UpsertLayerFieldAsync) currently bypass the V2 snapshot
    // and will return "Unknown fields include" until the V2 graph is rederived
    // from the v1 catalog or the fixtures are ported to publish via the V2 builder
    // directly. See task #55 (Port test fixtures off v1).
    private static bool TryBuildFieldSelection(
        MetadataV2Resource resource,
        StacFieldsExtension fields,
        out ImmutableArray<string> outFields,
        out StacFieldProjection? projection,
        out string? error)
    {
        var schemaFields = resource.SchemaFields ?? Array.Empty<MetadataV2Field>();
        var availableFields = schemaFields
            .Where(field => field.Type != MetadataV2FieldType.Geometry)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var includeAll = false;
        var requestedIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includedTopLevelFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludedTopLevelFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isIncludeMode = fields.Includes is { IsDefault: false, Length: > 0 };

        if (fields.Includes is { IsDefault: false } includes && includes.Length > 0)
        {
            foreach (var include in includes)
            {
                var isPropertyPath = IsPrefixedPropertyName(include);
                if (!TryNormalizePropertyName(include, out var normalized))
                {
                    error = $"Invalid fields include value '{include}'.";
                    outFields = default;
                    projection = null;
                    return false;
                }

                if (!isPropertyPath && IsTopLevelItemField(normalized))
                {
                    includedTopLevelFields.Add(normalized);
                    continue;
                }

                if (IsPropertiesWildcard(include))
                {
                    includeAll = true;
                    includedTopLevelFields.Add("properties");
                    continue;
                }

                if (!availableFields.ContainsKey(normalized))
                {
                    error = $"Unknown fields include '{include}'.";
                    outFields = default;
                    projection = null;
                    return false;
                }

                requestedIncludes.Add(normalized);
            }
        }
        else
        {
            includeAll = true;
        }

        if (fields.Excludes is { IsDefault: false } excludes && excludes.Length > 0)
        {
            foreach (var exclude in excludes)
            {
                var isPropertyPath = IsPrefixedPropertyName(exclude);
                if (!TryNormalizePropertyName(exclude, out var normalized))
                {
                    error = $"Invalid fields exclude value '{exclude}'.";
                    outFields = default;
                    projection = null;
                    return false;
                }

                if (!isPropertyPath && IsTopLevelItemField(normalized))
                {
                    excludedTopLevelFields.Add(normalized);
                    continue;
                }

                if (IsPropertiesWildcard(exclude))
                {
                    excludedTopLevelFields.Add("properties");
                    continue;
                }

                if (!availableFields.ContainsKey(normalized))
                {
                    error = $"Unknown fields exclude '{exclude}'.";
                    outFields = default;
                    projection = null;
                    return false;
                }

                requestedExcludes.Add(normalized);
            }
        }

        var resolvedSelection = includeAll
            ? availableFields.Keys.Where(field => !requestedExcludes.Contains(field))
            : requestedIncludes.Where(field => !requestedExcludes.Contains(field));

        var selected = resolvedSelection
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (selected.Length == 0 && !includeAll)
        {
            error = null;
            outFields = ImmutableArray<string>.Empty;
            projection = new StacFieldProjection(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                isIncludeMode,
                includedTopLevelFields,
                excludedTopLevelFields);
            return true;
        }

        var temporalFields = resource.ReadTemporalFields();
        var timeField = temporalFields.StartTimeField;
        var endTimeField = temporalFields.EndTimeField;
        var requiresUnprojectedAttributes = selected.Any(IsReservedAttributeProjectionName);
        var queryFields = requiresUnprojectedAttributes
            ? default
            : selected.Length == 0
                ? ImmutableArray<string>.Empty
                : schemaFields
                    .Where(field => selected.Contains(field.Name))
                    .Select(field => field.Name)
                    .ToImmutableArray();

        if (!requiresUnprojectedAttributes &&
            !string.IsNullOrWhiteSpace(timeField) &&
            !queryFields.Contains(timeField, StringComparer.OrdinalIgnoreCase))
        {
            queryFields = queryFields.Add(timeField!);
        }

        if (!requiresUnprojectedAttributes &&
            !string.IsNullOrWhiteSpace(endTimeField) &&
            !queryFields.Contains(endTimeField, StringComparer.OrdinalIgnoreCase))
        {
            queryFields = queryFields.Add(endTimeField!);
        }

        // When no explicit time fields are configured, the mapper falls back to well-known
        // temporal attribute names (created_at, updated_at, etc.). Ensure those candidates
        // are projected so datetime population still works under fields selection.
        if (!requiresUnprojectedAttributes && string.IsNullOrWhiteSpace(timeField))
        {
            ReadOnlySpan<string> fallbackCandidates =
            [
                "datetime", "created_at", "updated_at", "start_datetime",
                "end_datetime", "timestamp", "event_date", "date"
            ];
            foreach (var candidate in fallbackCandidates)
            {
                if (availableFields.TryGetValue(candidate, out var field) &&
                    field.Type is MetadataV2FieldType.Date
                        or MetadataV2FieldType.DateTime
                        or MetadataV2FieldType.Time &&
                    !queryFields.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    queryFields = queryFields.Add(candidate);
                }
            }
        }

        ReadOnlySpan<string> itemIdCandidates = ["stac_id", "item_id", "id"];
        if (!requiresUnprojectedAttributes)
        {
            foreach (var candidate in itemIdCandidates)
            {
                if (availableFields.ContainsKey(candidate) &&
                    !queryFields.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    queryFields = queryFields.Add(candidate);
                }
            }
        }

        outFields = queryFields;
        var selectedProperties = selected.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        if (selectedProperties.Count > 0)
        {
            includedTopLevelFields.Add("properties");
        }

        projection = new StacFieldProjection(
            selectedProperties,
            isIncludeMode,
            includedTopLevelFields,
            excludedTopLevelFields);
        error = null;
        return true;
    }

    private static StacItem ApplyFieldProjection(StacItem item, StacFieldProjection? projection)
    {
        if (projection is null || !projection.HasTopLevelRules)
        {
            return item;
        }

        return item with
        {
            StacVersion = ShouldIncludeTopLevelField(projection, "stac_version") ? item.StacVersion : null,
            Type = ShouldIncludeTopLevelField(projection, "type") ? item.Type : null,
            Id = ShouldIncludeTopLevelField(projection, "id") ? item.Id : null,
            Geometry = ShouldIncludeTopLevelField(projection, "geometry") ? item.Geometry : null,
            Bbox = ShouldIncludeTopLevelField(projection, "bbox") ? item.Bbox : null,
            Properties = ShouldIncludeTopLevelField(projection, "properties") ? item.Properties : null,
            Links = ShouldIncludeTopLevelField(projection, "links") ? item.Links : null,
            Assets = ShouldIncludeTopLevelField(projection, "assets") ? item.Assets : null,
            Collection = ShouldIncludeTopLevelField(projection, "collection") ? item.Collection : null,
            StacExtensions = ShouldIncludeTopLevelField(projection, "stac_extensions") ? item.StacExtensions : null
        };
    }

    private static bool ShouldIncludeTopLevelField(StacFieldProjection projection, string field)
        => projection.IsIncludeMode
            ? projection.IncludedTopLevelFields.Contains(field)
            : !projection.ExcludedTopLevelFields.Contains(field);

    private sealed record StacFieldProjection(
        IReadOnlySet<string>? SelectedProperties,
        bool IsIncludeMode,
        IReadOnlySet<string> IncludedTopLevelFields,
        IReadOnlySet<string> ExcludedTopLevelFields)
    {
        public bool HasTopLevelRules
            => IsIncludeMode || ExcludedTopLevelFields.Count > 0;
    }

    private static bool TryBuildSearchRequestFromGet(
        int? limit,
        string? datetime,
        string? bbox,
        string? collections,
        string? ids,
        string? intersects,
        string? fields,
        string? sortby,
        string? filter,
        string? filterLang,
        string? filterCrs,
        bool defaultFilterLangIsText,
        out StacSearchRequest request,
        out string? error)
    {
        request = new StacSearchRequest
        {
            Limit = limit,
            Datetime = datetime
        };
        error = null;

        if (!string.IsNullOrWhiteSpace(bbox))
        {
            if (!TryParseBbox(bbox, out var bboxValues, out error))
            {
                return false;
            }

            request = request with { Bbox = bboxValues };
        }

        if (!string.IsNullOrWhiteSpace(collections))
        {
            if (!TryParseIntegerTokenSet(collections, out var collectionValues, out error))
            {
                return false;
            }

            request = request with
            {
                Collections = collectionValues.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToImmutableArray()
            };
        }

        if (!string.IsNullOrWhiteSpace(ids))
        {
            if (!TryParseStringTokens(ids, out var idValues, out error))
            {
                return false;
            }

            request = request with { Ids = idValues };
        }

        if (!string.IsNullOrWhiteSpace(intersects))
        {
            if (!TryParseJsonElement(intersects, out var intersectsElement, out error))
            {
                return false;
            }

            request = request with { Intersects = intersectsElement };
        }

        if (!string.IsNullOrWhiteSpace(fields))
        {
            if (!TryParseFieldsParameter(fields, out var fieldsExtension, out error))
            {
                return false;
            }

            request = request with { Fields = fieldsExtension };
        }

        if (!string.IsNullOrWhiteSpace(sortby))
        {
            if (!TryParseSortByParameter(sortby, out var sortDefinitions, out error))
            {
                return false;
            }

            request = request with { Sortby = sortDefinitions };
        }

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var resolvedFilterLang = Cql2FilterProcessor.ResolveFilterLanguage(
                filterLang,
                CreateJsonStringElement(filter),
                defaultFilterLangIsText);
            if (resolvedFilterLang is null)
            {
                error = $"Invalid filter-lang '{filterLang}'.";
                return false;
            }

            if (resolvedFilterLang.Value == FilterLanguage.Cql2Json)
            {
                if (!TryParseJsonElement(filter, out var filterElement, out error))
                {
                    return false;
                }

                request = request with { Filter = filterElement, FilterLang = NormalizeFilterLang(filterLang), FilterCrs = filterCrs };
            }
            else
            {
                request = request with
                {
                    Filter = CreateJsonStringElement(filter),
                    FilterLang = NormalizeFilterLang(filterLang),
                    FilterCrs = filterCrs
                };
            }
        }
        else if (!string.IsNullOrWhiteSpace(filterLang) || !string.IsNullOrWhiteSpace(filterCrs))
        {
            error = "filter-lang and filter-crs require a filter parameter.";
            return false;
        }

        return true;
    }

    private static bool TryParseBbox(string bbox, out ImmutableArray<double> values, out string? error)
    {
        var parts = bbox.Split(',', StringSplitOptions.None | StringSplitOptions.TrimEntries);
        if (parts.Length is not 4 and not 6)
        {
            values = default;
            error = "bbox must contain four or six numeric values.";
            return false;
        }

        var parsed = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
            {
                values = default;
                error = "bbox contains an invalid numeric value.";
                return false;
            }
        }

        if (!StacFilterHelpers.TryExtractBbox2D(parsed, out _, out _, out _, out _, out error))
        {
            values = default;
            return false;
        }

        values = ImmutableArray.Create(parsed);
        error = null;
        return true;
    }

    private static bool TryParseCsvValues<T>(
        string value,
        TryParseDelegate<T> tryParse,
        out ImmutableArray<string> results,
        out string? error)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                results = default;
                error = "Parameter contains an empty value.";
                return false;
            }

            if (!tryParse(trimmed, out _))
            {
                results = default;
                error = $"Invalid value '{trimmed}'.";
                return false;
            }

            builder.Add(trimmed);
        }

        results = builder.ToImmutable();
        error = null;
        return true;
    }

    private static bool TryParseJsonElement(string value, out JsonElement element, out string? error)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            element = document.RootElement.Clone();
            error = null;
            return true;
        }
        catch (JsonException)
        {
            element = default;
            error = "Invalid JSON value.";
            return false;
        }
    }

    private static JsonElement CreateJsonStringElement(string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string? NormalizeFilterLang(string? filterLang)
    {
        if (string.IsNullOrWhiteSpace(filterLang))
        {
            return null;
        }

        return filterLang.Trim().ToLowerInvariant() switch
        {
            "cql2-text" => "cql2-text",
            "cql2-json" => "cql2-json",
            _ => null
        };
    }

    private static bool TryParseFieldsParameter(string fields, out StacFieldsExtension? result, out string? error)
    {
        var includeAll = false;
        var includes = ImmutableArray.CreateBuilder<string>();
        var excludes = ImmutableArray.CreateBuilder<string>();

        foreach (var token in fields.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                result = null;
                error = "fields contains an empty value.";
                return false;
            }

            var isExclude = trimmed[0] == '-';
            var isInclude = trimmed[0] == '+';
            var fieldExpression = (isExclude || isInclude ? trimmed[1..] : trimmed).Trim();
            if (!TryNormalizePropertyName(fieldExpression, out var normalized))
            {
                result = null;
                error = $"Invalid fields value '{trimmed}'.";
                return false;
            }

            if (IsPropertiesWildcard(fieldExpression))
            {
                includeAll = true;
                continue;
            }

            var fieldName = IsPrefixedPropertyName(fieldExpression)
                ? fieldExpression
                : normalized;

            if (isExclude)
            {
                excludes.Add(fieldName);
            }
            else
            {
                includes.Add(fieldName);
            }
        }

        if (includeAll && includes.Count == 0 && excludes.Count == 0)
        {
            result = null;
            error = null;
            return true;
        }

        result = new StacFieldsExtension
        {
            Includes = includes.Count > 0 ? includes.ToImmutable() : null,
            Excludes = excludes.Count > 0 ? excludes.ToImmutable() : null
        };
        error = null;
        return true;
    }

    private static bool TryParseSortByParameter(string sortby, out ImmutableArray<StacSortDefinition>? result, out string? error)
    {
        var builder = ImmutableArray.CreateBuilder<StacSortDefinition>();
        foreach (var token in sortby.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                result = null;
                error = "sortby contains an empty value.";
                return false;
            }

            var direction = "asc";
            var field = trimmed;
            if (trimmed[0] == '-')
            {
                direction = "desc";
                field = trimmed[1..];
            }
            else if (trimmed[0] == '+')
            {
                field = trimmed[1..];
            }

            if (string.IsNullOrWhiteSpace(field))
            {
                result = null;
                error = "sortby contains an empty field name.";
                return false;
            }

            builder.Add(new StacSortDefinition
            {
                Field = field,
                Direction = direction
            });
        }

        result = builder.ToImmutable();
        error = null;
        return true;
    }

    private static bool TryParseIntegerTokenSet(
        string value,
        out ImmutableArray<string> parsedValues,
        out string? error)
        => TryParseCsvValues(
            value,
            static (string input, out int parsed) => int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed),
            out parsedValues,
            out error);

    private static bool TryParseStringTokens(
        string value,
        out ImmutableArray<string> parsedValues,
        out string? error)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var token in value.Split(',', StringSplitOptions.None))
        {
            var trimmed = token.Trim();
            if (trimmed.Length == 0)
            {
                parsedValues = default;
                error = "ids contains an empty value.";
                return false;
            }

            builder.Add(trimmed);
        }

        parsedValues = builder.ToImmutable();
        error = null;
        return true;
    }

    private static bool TryParseIntegerTokenSet(
        ImmutableArray<string> values,
        out HashSet<int> parsedValues,
        out string? error)
    {
        parsedValues = new HashSet<int>();
        foreach (var value in values)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Invalid integer value '{value}'.";
                parsedValues = new HashSet<int>();
                return false;
            }

            parsedValues.Add(parsed);
        }

        error = null;
        return true;
    }

    private static bool TryNormalizeIdTokenSet(
        ImmutableArray<string> values,
        out ImmutableArray<string> normalizedValues,
        out string? error)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "ids contains an empty value.";
                normalizedValues = default;
                return false;
            }

            builder.Add(value.Trim());
        }

        normalizedValues = builder.ToImmutable();
        error = null;
        return true;
    }

    private static SqlFragment BuildItemIdsSqlFilter(ImmutableArray<string> itemIds)
    {
        var distinctIds = itemIds
            .Where(static itemId => !string.IsNullOrWhiteSpace(itemId))
            .Select(static itemId => itemId.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var numericObjectIds = distinctIds
            .Select(static itemId => long.TryParse(
                itemId,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var objectId)
                    ? objectId
                    : (long?)null)
            .Where(static objectId => objectId.HasValue)
            .Select(static objectId => objectId!.Value)
            .Distinct()
            .ToArray();

        var clauses = new List<string>
        {
            "attributes->>'stac_id' = ANY(@p0)",
            "attributes->>'item_id' = ANY(@p0)",
            "attributes->>'id' = ANY(@p0)"
        };
        var parameters = new List<object?> { distinctIds };

        if (numericObjectIds.Length > 0)
        {
            clauses.Add("objectid = ANY(@p1)");
            parameters.Add(numericObjectIds);
        }

        return new SqlFragment($"({string.Join(" OR ", clauses)})", parameters);
    }

    private static SqlFragment CombineSqlFilters(SqlFragment? left, SqlFragment right)
    {
        if (left is null)
        {
            return right;
        }

        var rightSql = RenumberSqlFragmentParameters(right.Sql, left.Parameters.Count);
        return new SqlFragment(
            $"({left.Sql}) AND ({rightSql})",
            left.Parameters.Concat(right.Parameters).ToArray());
    }

    private static string RenumberSqlFragmentParameters(string sql, int offset)
        => Regex.Replace(
            sql,
            @"@p(\d+)",
            match => "@p" + (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) + offset).ToString(CultureInfo.InvariantCulture),
            RegexOptions.CultureInvariant);

    private static bool TryNormalizePropertyName(string name, out string normalized)
    {
        normalized = name.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (normalized.StartsWith("properties.", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized["properties.".Length..];
        }

        return normalized.Length > 0;
    }

    private static bool IsPrefixedPropertyName(string name)
        => name.Trim().StartsWith("properties.", StringComparison.OrdinalIgnoreCase);

    private static bool IsPropertiesWildcard(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Equals("properties", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("*", StringComparison.Ordinal) ||
            trimmed.Equals("properties.*", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReservedAttributeProjectionName(string name)
        => name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("object_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("layer_id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("attributes", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("created_at", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("updated_at", StringComparison.OrdinalIgnoreCase);

    private static bool IsTopLevelItemField(string name)
        => name.Equals("geometry", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("bbox", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("links", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("collection", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("stac_version", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("stac_extensions", StringComparison.OrdinalIgnoreCase);

    private static string BuildSearchQuery(int limit, int offset, StacSearchRequest request, bool defaultFilterLangIsText)
    {
        var query = new List<string>
        {
            $"limit={limit}",
            $"offset={offset}"
        };

        if (request.Bbox is { IsDefault: false } bboxArr && bboxArr.Length >= 4)
        {
            query.Add("bbox=" + string.Join(",", bboxArr.Select(v => v.ToString(CultureInfo.InvariantCulture))));
        }

        if (!string.IsNullOrWhiteSpace(request.Datetime))
        {
            query.Add($"datetime={Uri.EscapeDataString(request.Datetime)}");
        }

        if (request.Collections is { IsDefault: false } cols && cols.Length > 0)
        {
            query.Add($"collections={Uri.EscapeDataString(string.Join(",", cols))}");
        }

        if (request.Ids is { IsDefault: false } ids && ids.Length > 0)
        {
            query.Add($"ids={Uri.EscapeDataString(string.Join(",", ids))}");
        }

        if (request.Intersects.HasValue)
        {
            query.Add($"intersects={Uri.EscapeDataString(request.Intersects.Value.GetRawText())}");
        }

        if (request.Fields is not null)
        {
            var fieldTokens = new List<string>();
            if (request.Fields.Includes is { IsDefault: false } includes && includes.Length > 0)
            {
                fieldTokens.AddRange(includes);
            }

            if (request.Fields.Excludes is { IsDefault: false } excludes && excludes.Length > 0)
            {
                fieldTokens.AddRange(excludes.Select(field => "-" + field));
            }

            if (fieldTokens.Count > 0)
            {
                query.Add($"fields={Uri.EscapeDataString(string.Join(",", fieldTokens))}");
            }
        }

        if (request.Sortby is { IsDefault: false } sortby && sortby.Length > 0)
        {
            var sortTokens = sortby.Select(sort => string.Equals(sort.Direction, "desc", StringComparison.OrdinalIgnoreCase)
                ? "-" + sort.Field
                : sort.Field);
            query.Add($"sortby={Uri.EscapeDataString(string.Join(",", sortTokens))}");
        }

        if (request.Filter.HasValue)
        {
            query.Add($"filter={Uri.EscapeDataString(
                request.Filter.Value.ValueKind == JsonValueKind.String
                    ? request.Filter.Value.GetString() ?? string.Empty
                    : request.Filter.Value.GetRawText())}");

            var filterLang = NormalizeFilterLang(request.FilterLang)
                ?? (defaultFilterLangIsText
                    ? "cql2-text"
                    : request.Filter.Value.ValueKind == JsonValueKind.String
                        ? "cql2-text"
                        : "cql2-json");
            query.Add($"filter-lang={filterLang}");
        }

        if (!string.IsNullOrWhiteSpace(request.FilterCrs))
        {
            query.Add($"filter-crs={Uri.EscapeDataString(request.FilterCrs)}");
        }

        return string.Join("&", query);
    }
}

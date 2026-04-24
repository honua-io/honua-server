// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Filtering;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Stac.Models;
using Honua.Server.Features.Protocols.Stac.Services;
using Honua.Core.Features.Geometry.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Stac;

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
        [FromQuery] string? intersects,
        [FromQuery] string? fields,
        [FromQuery] string? sortby,
        [FromQuery(Name = "filter")] string? filter,
        [FromQuery(Name = "filter-lang")] string? filterLang,
        [FromQuery(Name = "filter-crs")] string? filterCrs,
        [FromServices] StacSearchDependencies deps)
    {
        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, StacConstants.AllowedQueryParameters.SearchGet);
        if (validationError is not null)
        {
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
            return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid search parameters.");
        }

        var effectiveOffset = Math.Max(offset ?? 0, 0);

        return await ExecuteSearchAsync(
            request,
            effectiveOffset,
            context,
            deps.LayerCatalog,
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
        return await ExecuteSearchAsync(
            request,
            0,
            context,
            deps.LayerCatalog,
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
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
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

            if (request.Collections is { IsDefault: false } requestedCollections && requestedCollections.Length > 0)
            {
                if (!TryParseIntegerTokenSet(requestedCollections, out var collectionIds, out var collectionError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context, collectionError ?? "Invalid collections parameter.");
                }

                targetLayers = targetLayers.Where(l => collectionIds.Contains(l.Id));
            }

            if (request.Ids is { IsDefault: false } requestedIds && requestedIds.Length > 0)
            {
                if (!TryNormalizeIdTokenSet(requestedIds, out var normalizedIds, out var idsError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context, idsError ?? "Invalid ids parameter.");
                }

                var layerList = targetLayers.ToArray();
                return await ExecuteSearchAcrossLayersAsync(
                    request,
                    offset,
                    context,
                    baseUrl,
                    featureReader,
                    geometryService,
                    filterProcessor,
                    defaultFilterLangIsText,
                    logger,
                    layerList,
                    normalizedIds,
                    effectiveLimit);
            }

            return await ExecuteSearchAcrossLayersAsync(
                request,
                offset,
                context,
                baseUrl,
                featureReader,
                geometryService,
                filterProcessor,
                defaultFilterLangIsText,
                logger,
                targetLayers.ToArray(),
                null,
                effectiveLimit);
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

    private static async Task<IResult> ExecuteSearchAcrossLayersAsync(
        StacSearchRequest request,
        int offset,
        HttpContext context,
        string baseUrl,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        ILogger logger,
        Core.Features.Catalog.Domain.LayerDefinition[] layerList,
        ImmutableArray<string>? requestedItemIds,
        int effectiveLimit)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var allItems = ImmutableArray.CreateBuilder<StacItem>();
        long totalMatched = 0;
        var remainingSkip = offset;
        var hasEmptyIdFilter = requestedItemIds is { Length: 0 };
        var layerSelections = new Dictionary<int, IReadOnlySet<string>?>();

        foreach (var layer in layerList)
        {
            if (hasEmptyIdFilter)
            {
                break;
            }

            var layerQueryResult = await TryBuildLayerQuery(
                request,
                layer,
                requestedItemIds,
                geometryService,
                filterProcessor,
                defaultFilterLangIsText,
                cancellationToken);
            if (!layerQueryResult.IsSuccess)
            {
                return StandardErrorHelpers.CreateBadRequest(context, layerQueryResult.Error ?? "Invalid search parameters.");
            }

            var query = layerQueryResult.Query;
            var selectedProperties = layerQueryResult.SelectedProperties;
            layerSelections[layer.Id] = selectedProperties;

            if (remainingSkip > 0)
            {
                var layerCount = await featureReader.CountAsync(layer.Id, query, cancellationToken);
                totalMatched += layerCount;

                if (remainingSkip >= layerCount)
                {
                    remainingSkip -= (int)Math.Min(layerCount, int.MaxValue);
                    continue;
                }

                var remaining = effectiveLimit - allItems.Count;
                query = query with { Offset = remainingSkip, Limit = remaining };
                remainingSkip = 0;

                var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                allItems.AddRange(result.Features
                    .Select(f => StacMappingService.MapFeatureToItem(
                        f,
                        layer,
                        baseUrl,
                        selectedProperties,
                        geometrySrid: Wgs84Srid)));
            }
            else if (allItems.Count < effectiveLimit)
            {
                var remaining = effectiveLimit - allItems.Count;
                query = query with { Limit = remaining };

                var result = await featureReader.QueryAsync(layer.Id, query, cancellationToken);
                totalMatched += result.TotalCount;

                allItems.AddRange(result.Features
                    .Select(f => StacMappingService.MapFeatureToItem(
                        f,
                        layer,
                        baseUrl,
                        selectedProperties,
                        geometrySrid: Wgs84Srid)));
            }
            else
            {
                totalMatched += await featureReader.CountAsync(layer.Id, query, cancellationToken);
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
        return Results.Json(response, StacJsonContext.Default.StacItemCollection, MediaTypes.GeoJson);
    }

    private static async Task<(bool IsSuccess, FeatureQuery Query, IReadOnlySet<string>? SelectedProperties, string? Error)> TryBuildLayerQuery(
        StacSearchRequest request,
        Core.Features.Catalog.Domain.LayerDefinition layer,
        ImmutableArray<string>? requestedItemIds,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken)
    {
        var query = new FeatureQuery
        {
            SpatialReferenceSrid = layer.SpatialReference.Wkid,
            OutputSrid = Wgs84Srid
        };
        IReadOnlySet<string>? selectedProperties = null;
        string? error = null;

        if (request.Bbox is { IsDefault: false } bboxArr)
        {
            if (bboxArr.Length != 4)
            {
                error = "3D bbox values are not supported.";
                return (false, query, selectedProperties, error);
            }

            if (!StacFilterHelpers.TryValidateBboxCoordinates(
                    bboxArr[0], bboxArr[1], bboxArr[2], bboxArr[3],
                    out error))
            {
                return (false, query, selectedProperties, error);
            }

            if (request.Intersects.HasValue)
            {
                error = "bbox and intersects cannot be combined.";
                return (false, query, selectedProperties, error);
            }

            query = query with
            {
                SpatialFilter = StacFilterHelpers.CreateBboxSpatialFilter(
                    west: bboxArr[0], south: bboxArr[1], east: bboxArr[2], north: bboxArr[3])
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
                return (false, query, selectedProperties, error);
            }

            if (intersectsSpatialFilter.HasValue)
            {
                query = query with { SpatialFilter = intersectsSpatialFilter };
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Datetime))
        {
            var temporalFilter = StacFilterHelpers.ParseDatetime(request.Datetime, layer);
            if (temporalFilter is null)
            {
                error = "Invalid datetime parameter.";
                return (false, query, selectedProperties, error);
            }

            query = query with { TemporalFilter = temporalFilter };
        }

        var filterQueryResult = await TryResolveFilterQuery(
            request,
            layer,
            filterProcessor,
            defaultFilterLangIsText,
            cancellationToken);
        if (!filterQueryResult.IsSuccess)
        {
            error = filterQueryResult.Error;
            return (false, query, selectedProperties, error);
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
            if (!TryBuildSortOrder(layer, sortby, out var orderBy, out var sortError))
            {
                error = sortError;
                return (false, query, selectedProperties, error);
            }

            query = query with { OrderBy = orderBy };
        }

        if (request.Fields is not null)
        {
            if (!TryBuildFieldSelection(layer, request.Fields, out var outFields, out selectedProperties, out var fieldError))
            {
                error = fieldError;
                return (false, query, selectedProperties, error);
            }

            query = query with { OutFields = outFields };
        }

        return (true, query, selectedProperties, null);
    }

    private static async Task<(bool IsSuccess, SqlFragment? SqlFilter, string? Error)> TryResolveFilterQuery(
        StacSearchRequest request,
        Core.Features.Catalog.Domain.LayerDefinition layer,
        Cql2FilterProcessor filterProcessor,
        bool defaultFilterLangIsText,
        CancellationToken cancellationToken)
    {
        var result = await filterProcessor.ProcessFilterAsync(
            layer,
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
        Core.Features.Catalog.Domain.LayerDefinition layer,
        ImmutableArray<StacSortDefinition> sortby,
        out ImmutableArray<OrderByClause> orderBy,
        out string? error)
    {
        var availableFields = layer.AttributeFields.ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);
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

            orderByBuilder.Add(
                string.Equals(sort.Direction, "desc", StringComparison.OrdinalIgnoreCase)
                    ? OrderByClause.Desc(normalizedField)
                    : string.Equals(sort.Direction, "asc", StringComparison.OrdinalIgnoreCase)
                        ? OrderByClause.Asc(normalizedField)
                        : throw new ArgumentException($"Invalid sort direction '{sort.Direction}'."));
        }

        error = null;
        orderBy = orderByBuilder.ToImmutable();
        return true;
    }

    private static bool TryBuildFieldSelection(
        Core.Features.Catalog.Domain.LayerDefinition layer,
        StacFieldsExtension fields,
        out ImmutableArray<string> outFields,
        out IReadOnlySet<string>? selectedProperties,
        out string? error)
    {
        var availableFields = layer.AttributeFields
            .Where(field => !field.IsGeometry)
            .ToDictionary(field => field.Name, StringComparer.OrdinalIgnoreCase);

        var includeAll = false;
        var requestedIncludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requestedExcludes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (fields.Includes is { IsDefault: false } includes && includes.Length > 0)
        {
            foreach (var include in includes)
            {
                if (!TryNormalizePropertyName(include, out var normalized))
                {
                    error = $"Invalid fields include value '{include}'.";
                    outFields = default;
                    selectedProperties = null;
                    return false;
                }

                if (IsPropertiesSentinel(normalized))
                {
                    includeAll = true;
                    continue;
                }

                if (!availableFields.ContainsKey(normalized))
                {
                    error = $"Unknown fields include '{include}'.";
                    outFields = default;
                    selectedProperties = null;
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
                if (!TryNormalizePropertyName(exclude, out var normalized))
                {
                    error = $"Invalid fields exclude value '{exclude}'.";
                    outFields = default;
                    selectedProperties = null;
                    return false;
                }

                if (IsPropertiesSentinel(normalized))
                {
                    continue;
                }

                if (!availableFields.ContainsKey(normalized))
                {
                    error = $"Unknown fields exclude '{exclude}'.";
                    outFields = default;
                    selectedProperties = null;
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
            selectedProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        var timeField = layer.Metadata?.TimeInfo?.StartTimeField;
        var queryFields = selected.Length == 0
            ? ImmutableArray<string>.Empty
            : layer.AttributeFields
                .Where(field => selected.Contains(field.Name))
                .Select(field => field.Name)
                .ToImmutableArray();

        if (!string.IsNullOrWhiteSpace(timeField) && !queryFields.Contains(timeField, StringComparer.OrdinalIgnoreCase))
        {
            queryFields = queryFields.Add(timeField!);
        }

        var endTimeField = layer.Metadata?.TimeInfo?.EndTimeField;
        if (!string.IsNullOrWhiteSpace(endTimeField) && !queryFields.Contains(endTimeField, StringComparer.OrdinalIgnoreCase))
        {
            queryFields = queryFields.Add(endTimeField!);
        }

        // When no explicit time fields are configured, the mapper falls back to well-known
        // temporal attribute names (created_at, updated_at, etc.). Ensure those candidates
        // are projected so datetime population still works under fields selection.
        if (string.IsNullOrWhiteSpace(timeField))
        {
            ReadOnlySpan<string> fallbackCandidates =
            [
                "datetime", "created_at", "updated_at", "start_datetime",
                "end_datetime", "timestamp", "event_date", "date"
            ];
            foreach (var candidate in fallbackCandidates)
            {
                if (availableFields.ContainsKey(candidate) &&
                    !queryFields.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    queryFields = queryFields.Add(candidate);
                }
            }
        }

        ReadOnlySpan<string> itemIdCandidates = ["stac_id", "item_id", "id"];
        foreach (var candidate in itemIdCandidates)
        {
            if (availableFields.ContainsKey(candidate) &&
                !queryFields.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                queryFields = queryFields.Add(candidate);
            }
        }

        outFields = queryFields;
        selectedProperties = selected.Length == 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        error = null;
        return true;
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

        if (parts.Length == 6)
        {
            values = default;
            error = "3D bbox values are not supported.";
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

        if (!StacFilterHelpers.TryValidateBboxCoordinates(parsed[0], parsed[1], parsed[2], parsed[3], out error))
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
            var normalized = isExclude ? trimmed[1..] : trimmed;
            if (!TryNormalizePropertyName(normalized, out normalized))
            {
                result = null;
                error = $"Invalid fields value '{trimmed}'.";
                return false;
            }

            if (IsPropertiesSentinel(normalized))
            {
                includeAll = true;
                continue;
            }

            if (isExclude)
            {
                excludes.Add(normalized);
            }
            else
            {
                includes.Add(normalized);
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

    private static bool IsPropertiesSentinel(string name)
        => name.Equals("properties", StringComparison.OrdinalIgnoreCase) ||
           name.Equals("*", StringComparison.Ordinal);

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

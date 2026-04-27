// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.OData;

/// <summary>
/// Handler for OData query operations on layers and features.
/// Handles standard OData query parameters ($filter, $select, $orderby, $top, $skip, $count, $expand).
/// </summary>
internal sealed partial class ODataQueryHandler(
    ODataQueryDependencies dependencies,
    ILogger<ODataQueryHandler> logger)
{
    private readonly ILayerCatalog _layerCatalog = dependencies?.LayerCatalog
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IGeometryService _geometryService = dependencies.GeometryService;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly ODataValidationService _validationService = dependencies.ValidationService;
    private readonly ODataQuerySearchService _querySearchService = dependencies.QuerySearchService;
    private readonly IResponseCache _responseCache = dependencies.ResponseCache;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly ILogger<ODataQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles OData layers collection request
    /// </summary>
    public async Task<IResult> HandleGetLayersAsync(
        HttpContext context,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] string? top = null,
        [FromQuery(Name = "$skip")] string? skip = null,
        [FromQuery(Name = "$skiptoken")] string? skiptoken = null,
        [FromQuery(Name = "$count")] string? count = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Layers);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(select))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$select contains an empty field expression.");
            }

            var pagingError = ODataRequestValidation.TryGetPagingValues(
                context,
                _validationService,
                top,
                skip,
                skiptoken,
                count,
                out var pagination,
                out _,
                out _,
                out var countValue,
                out _);
            if (pagingError != null)
            {
                return pagingError;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var visibleLayers = await GetVisibleODataLayersAsync(context, effectiveToken);

            // Apply filtering and processing
            IEnumerable<LayerDefinition> layerQuery = visibleLayers;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = _querySearchService.ApplyBasicFilter((IEnumerable<Core.Features.Catalog.Domain.LayerDefinition>)layerQuery, filter);
            }

            if (!string.IsNullOrWhiteSpace(orderby))
            {
                layerQuery = ApplyLayerOrdering(layerQuery, orderby);
            }

            // Apply pagination and counting
            long? totalCount = null;
            if (countValue == true)
            {
                var layersForCount = layerQuery.ToArray();
                totalCount = layersForCount.Length;
                layerQuery = layersForCount;
            }

            if (pagination.Offset > 0)
            {
                layerQuery = layerQuery.Skip(pagination.Offset);
            }

            layerQuery = layerQuery.Take(pagination.Limit);

            // Convert to response format
            var layerData = layerQuery
                .Select(ODataUtilityService.BuildLayerPayload)
                .ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? layerData.Cast<object>().ToArray()
                : _querySearchService.ApplyFieldSelection(layerData, select);

            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
            var includeContext = ODataUtilityService.ShouldIncludeContext(context.Request, format);
            var response = ODataUtilityService.CreateODataResponse(
                baseUrl,
                "Layers",
                result,
                totalCount,
                select: select,
                includeContext: includeContext);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse,
                contentType: ODataUtilityService.GetODataContentType(context.Request, format));
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidLayersQuery(_logger, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.LayersQueryFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles OData single layer request
    /// </summary>
    public async Task<IResult> HandleGetLayerAsync(
        HttpContext context,
        int layerId,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Layer);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(select))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$select contains an empty field expression.");
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;

            var payload = ODataUtilityService.BuildLayerPayload(layer);
            if (!string.IsNullOrWhiteSpace(select))
            {
                var selected = _querySearchService.ApplyFieldSelection(new[] { payload }, select);
                payload = selected.Length > 0 && selected[0] is Dictionary<string, object?> selectedPayload
                    ? selectedPayload
                    : payload;
            }

            if (ODataUtilityService.ShouldIncludeContext(context.Request, format))
            {
                payload["@odata.context"] = ODataUtilityService.BuildContextUrl(
                    ODataUtilityService.GetBaseUrl(context.Request),
                    "Layers",
                    isSingle: true,
                    select: select);
            }

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(payload, ODataJsonContext.Default.DictionaryStringObject,
                contentType: ODataUtilityService.GetODataContentType(context.Request, format));
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidLayersQuery(_logger, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.LayersQueryFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles OData layers $count request
    /// </summary>
    public async Task<IResult> HandleGetLayersCountAsync(
        HttpContext context,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$format")] string? format = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.LayersCount);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var formatValidation = ODataRequestValidation.ValidateFormat(context, _validationService, format);
            if (formatValidation != null)
            {
                return formatValidation;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var visibleLayers = await GetVisibleODataLayersAsync(context, effectiveToken);

            IEnumerable<LayerDefinition> layerQuery = visibleLayers;
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = _querySearchService.ApplyBasicFilter(layerQuery, filter);
            }

            var count = layerQuery.LongCount();

            ODataUtilityService.SetODataHeaders(context);
            return Results.Text(count.ToString(CultureInfo.InvariantCulture), "text/plain");
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidLayersQuery(_logger, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.LayersQueryFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
    }

    /// <summary>
    /// Handles OData features collection request with non-streaming implementation for small result sets
    /// </summary>
    public async Task<IResult> HandleGetFeaturesNonStreamingAsync(
        HttpContext context,
        int layerId,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int? top = null,
        [FromQuery(Name = "$skip")] int? skip = null,
        [FromQuery(Name = "$count")] bool? count = null,
        [FromQuery(Name = "$expand")] string? expand = null,
        bool useSkipToken = false,
        [FromQuery(Name = "$compute")] string? compute = null,
        [FromQuery(Name = "$format")] string? format = null,
        DateTimeOffset? deltaSince = null,
        bool trackChangesRequested = false,
        string? deltatoken = null,
        ODataDeltaService.DeltaQueryState? deltaState = null,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.Features);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(select))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$select contains an empty field expression.");
            }

            if (ODataParsingUtilities.HasEmptyCommaSeparatedToken(expand))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$expand contains an empty navigation expression.");
            }

            if (!ODataComputeService.TryParse(compute, out var computeExpressions, out var computeError))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    computeError ?? "Invalid $compute expression.");
            }

            var paginationResult = _validationService.ValidateAndNormalizePagination(skip, top);
            if (!paginationResult.IsValid)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQueryOption",
                    paginationResult.ErrorMessage ?? "Invalid OData query.");
            }

            var pagination = paginationResult.Value!;
            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessAsync(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.OData,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            var effectiveFilter = deltaSince.HasValue
                ? ODataDeltaService.BuildDeltaFilter(filter, deltaSince.Value, deltaState?.UpperBoundTimestamp)
                : filter;

            // Build feature query using query service
            var (featureQuery, queryError) = await _querySearchService.BuildFeatureQueryAsync(
                effectiveFilter,
                orderby,
                pagination.Limit,
                pagination.Offset,
                layer,
                select,
                expand,
                count,
                compute,
                format,
                effectiveToken);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            var canCache = ResponseCacheUtilities.ShouldCache(context, _cacheOptions) &&
                           string.IsNullOrWhiteSpace(expand) &&
                           string.IsNullOrWhiteSpace(deltatoken) &&
                           !trackChangesRequested &&
                           !ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(featureQuery, filter) &&
                           !AcceptRequestsNonDefaultMetadata(context.Request, format);
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;
            if (canCache && cacheTtl <= TimeSpan.Zero)
            {
                canCache = false;
            }

            var cacheKey = canCache
                ? ResponseCacheUtilities.BuildODataLayerKey(layerId, context.Request)
                : null;

            if (canCache && cacheKey != null)
            {
                var cached = await _responseCache.GetAsync<CachedResponse>(cacheKey, effectiveToken);
                if (cached != null)
                {
                    ODataUtilityService.SetODataHeaders(context);
                    if (trackChangesRequested)
                    {
                        ODataUtilityService.ApplyTrackChangesPreference(context);
                    }

                    HonuaTelemetry.SetSuccess(featureActivity);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
                }
            }

            // Execute query
            var queryResult = await _featureReader.QueryAsync(layerId, featureQuery, effectiveToken);

            // Process $expand for related entities
            Dictionary<long, Dictionary<string, object?[]>>? expandedRelations = null;
            if (!string.IsNullOrWhiteSpace(expand) && layer.HasRelationships)
            {
                expandedRelations = await _querySearchService.ProcessExpandAsync(
                    expand,
                    layer,
                    queryResult.Items.Select(f => f.Id).ToArray(),
                    context,
                    effectiveToken);
            }

            var layerSrid = layer.SpatialReference.ToSrid();
            var axisOrder = await ResolveAxisOrderAsync(layerSrid, effectiveToken);

            // Convert features to OData format
            var featuresData = queryResult.Items.Select(f =>
            {
                var attributes = ODataAttributeSerializer.Serialize(f.Attributes);
                var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
                    _geometryService,
                    f.Geometry,
                    layerSrid,
                    axisOrder);
                var dict = ODataUtilityService.BuildFeaturePayload(layerId, f, geometry, attributes);

                // Add expanded relations if available
                if (expandedRelations != null && expandedRelations.TryGetValue(f.Id, out var relations))
                {
                    foreach (var kvp in relations)
                    {
                        dict[kvp.Key] = kvp.Value;
                    }
                }

                ODataComputeService.ApplyCompute(dict, computeExpressions);
                return dict;
            }).ToArray();

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? featuresData.Cast<object>().ToArray()
                : _querySearchService.ApplyFieldSelection(featuresData, select);

            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            // Calculate @odata.nextLink if there are more results
            string? nextLink = null;
            if (ODataUtilityService.ShouldPaginate(result.Length, pagination.Offset, queryResult.TotalCount, pagination.Limit))
            {
                var nextSkip = ODataUtilityService.CalculateNextSkip(pagination.Offset, pagination.Limit);
                nextLink = !string.IsNullOrWhiteSpace(deltatoken)
                    ? ODataUtilityService.GenerateDeltaNextLink(
                        context.Request,
                        deltaState ?? throw new InvalidOperationException("Delta state is required for delta paging."),
                        nextSkip,
                        pagination.Limit,
                        useSkipToken,
                        deltaState?.Filter,
                        deltaState?.OrderBy)
                    : ODataUtilityService.GenerateNextLink(
                        context.Request,
                        nextSkip,
                        pagination.Limit,
                        filter,
                        select,
                        orderby,
                        count,
                        expand,
                        useSkipToken,
                        compute,
                        format,
                        trackChangesRequested,
                        trackChangesRequested ? deltaState : null);
            }

            string? deltaLink = null;
            if (nextLink == null && trackChangesRequested)
            {
                var baseDeltaState = deltaState ?? new ODataDeltaService.DeltaQueryState
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    LayerId = layerId,
                    Filter = filter,
                    Select = select,
                    OrderBy = orderby,
                    Expand = expand,
                    Compute = compute,
                    Format = format,
                    Count = count
                };
                var finalDeltaState = !string.IsNullOrWhiteSpace(deltatoken)
                    ? baseDeltaState with
                    {
                        Timestamp = baseDeltaState.UpperBoundTimestamp ?? DateTimeOffset.UtcNow,
                        UpperBoundTimestamp = null,
                        LayerId = layerId,
                        Count = count
                    }
                    : baseDeltaState with
                    {
                        LayerId = layerId,
                        Count = count
                    };
                deltaLink = ODataUtilityService.GenerateDeltaLink(context.Request, finalDeltaState);
            }

            var response = new ODataResponse
            {
                Context = ODataUtilityService.ShouldIncludeContext(context.Request, format)
                    ? ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: select, expand: expand)
                    : null,
                Count = count == true ? queryResult.TotalCount : null,
                NextLink = nextLink,
                DeltaLink = deltaLink,
                Value = result
            };

            ODataUtilityService.SetODataHeaders(context);
            if (trackChangesRequested)
            {
                ODataUtilityService.ApplyTrackChangesPreference(context);
            }

            var contentType = ODataUtilityService.GetODataContentType(context.Request, format);
            if (canCache && cacheKey != null)
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(response, ODataJsonContext.Default.ODataResponse);
                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, effectiveToken);
                HonuaTelemetry.SetSuccess(featureActivity, result.Length);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            HonuaTelemetry.SetSuccess(featureActivity, result.Length);
            return Results.Json(response, ODataJsonContext.Default.ODataResponse,
                contentType: contentType);
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (ArgumentException ex)
        {
            Log.InvalidFeaturesQuery(_logger, layerId, ex);
            var safeDetail = ExceptionMapper.Map(ex).Detail;
            HonuaTelemetry.RecordException(featureActivity, ex);
            return ODataUtilityService.CreateODataError(context, "InvalidQuery", safeDetail);
        }
        catch (Exception ex)
        {
            Log.FeaturesQueryFailed(_logger, layerId, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the OData request", 500);
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private async Task<LayerDefinition[]> GetVisibleODataLayersAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var layers = await _layerCatalog.ListLayersAsync(cancellationToken);
        var services = await _layerCatalog.ListServicesAsync(cancellationToken);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap(services, ServiceProtocols.OData);

        return layers
            .Where(layer => IsODataLayerVisible(context, layer, primaryServices))
            .ToArray();
    }

    private static bool IsODataLayerVisible(
        HttpContext context,
        LayerDefinition layer,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
    {
        if (primaryServices.TryGetValue(layer.Id, out var service) &&
            ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.OData))
        {
            return AccessPolicyHelpers.IsLayerAccessible(context, layer, service);
        }

        return ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.OData) &&
            AccessPolicyHelpers.IsLayerAccessible(context, layer);
    }

    private ValueTask<AxisOrder> ResolveAxisOrderAsync(int? srid, CancellationToken cancellationToken)
    {
        return ODataCrsUtilities.ResolveAxisOrderAsync(_crsRegistry, srid, cancellationToken);
    }

    private static IEnumerable<LayerDefinition> ApplyLayerOrdering(IEnumerable<LayerDefinition> layers, string orderby)
    {
        IOrderedEnumerable<LayerDefinition>? ordered = null;

        foreach (var segment in orderby.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var parts = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts.Length > 2)
            {
                throw new ArgumentException("Invalid $orderby expression.");
            }

            var field = parts[0];
            var ascending = parts.Length == 1 || parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 2 &&
                !parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase) &&
                !parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Invalid sort direction in $orderby: {parts[1]}. Use 'asc' or 'desc'.");
            }

            ordered = ApplyLayerOrderingClause(ordered, layers, field, ascending);
        }

        return ordered ?? layers;
    }

    private static IOrderedEnumerable<LayerDefinition> ApplyLayerOrderingClause(
        IOrderedEnumerable<LayerDefinition>? ordered,
        IEnumerable<LayerDefinition> layers,
        string field,
        bool ascending)
    {
        return field.ToLowerInvariant() switch
        {
            "id" => ApplyOrder(ordered, layers, static layer => layer.Id, ascending),
            "name" => ApplyOrder(ordered, layers, static layer => layer.Name ?? string.Empty, ascending),
            "description" => ApplyOrder(ordered, layers, static layer => layer.Description ?? string.Empty, ascending),
            "geometrytype" => ApplyOrder(ordered, layers, static layer => layer.GeometryType.ToString(), ascending),
            "srid" => ApplyOrder(ordered, layers, static layer => layer.SpatialReference.ToSrid(), ascending),
            _ => throw new ArgumentException($"Unknown field in $orderby: {field}")
        };
    }

    private static IOrderedEnumerable<LayerDefinition> ApplyOrder<TKey>(
        IOrderedEnumerable<LayerDefinition>? ordered,
        IEnumerable<LayerDefinition> layers,
        Func<LayerDefinition, TKey> selector,
        bool ascending)
    {
        if (ordered != null)
        {
            return ascending
                ? ordered.ThenBy(selector)
                : ordered.ThenByDescending(selector);
        }

        return ascending
            ? layers.OrderBy(selector)
            : layers.OrderByDescending(selector);
    }

    private static bool AcceptRequestsNonDefaultMetadata(HttpRequest request, string? format)
    {
        if (!string.IsNullOrWhiteSpace(format) &&
            ContainsMetadataFormatParameter(format) &&
            !format.Contains("minimal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var accept = request.Headers.Accept.ToString();
        return !string.IsNullOrWhiteSpace(accept) &&
               ContainsMetadataFormatParameter(accept) &&
               !ContainsMinimalMetadataFormatParameter(accept);
    }

    private static bool ContainsMetadataFormatParameter(string value)
        => value.Contains("metadata=", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("odata.metadata=", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsMinimalMetadataFormatParameter(string value)
        => value.Contains("metadata=minimal", StringComparison.OrdinalIgnoreCase) ||
           value.Contains("odata.metadata=minimal", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Logging methods for OData query operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Invalid OData layers query.")]
        public static partial void InvalidLayersQuery(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "OData layers query failed.")]
        public static partial void LayersQueryFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3003, Level = LogLevel.Warning, Message = "Invalid OData features query for layer {LayerId}.")]
        public static partial void InvalidFeaturesQuery(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(EventId = 3004, Level = LogLevel.Error, Message = "OData features query failed for layer {LayerId}.")]
        public static partial void FeaturesQueryFailed(ILogger logger, int layerId, Exception exception);
    }
}

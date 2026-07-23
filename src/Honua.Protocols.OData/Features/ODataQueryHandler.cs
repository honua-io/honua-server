// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.OData;

/// <summary>
/// Handler for OData query operations on layers and features.
/// Handles standard OData query parameters ($filter, $select, $orderby, $top, $skip, $count, $expand).
/// </summary>
internal sealed partial class ODataQueryHandler(
    ODataQueryDependencies dependencies,
    ILogger<ODataQueryHandler> logger)
{
    private const string ODataProtocol = "OData";

    private readonly IFeatureReader _featureReader = (dependencies
        ?? throw new ArgumentNullException(nameof(dependencies))).FeatureReader;
    private readonly ODataFeatureProviderResolver? _featureProviderResolver = dependencies.FeatureProviderResolver;
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

            // BH-S-04: /Layers uses $skip (not $skiptoken) for continuation; the server never
            // emits a $skiptoken nextLink for the catalog layer list.  Accepting an inbound
            // $skiptoken here would validate it against a null-fingerprint, which accepts any
            // token issued by any Features endpoint with no filter/orderby — an isolation bug.
            if (!string.IsNullOrWhiteSpace(skiptoken))
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InvalidQueryOption",
                    "$skiptoken is not supported on /Layers. Use $skip for catalog layer pagination.");
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
            IEnumerable<Dictionary<string, object?>> layerQuery = visibleLayers
                .Select(layer => ODataUtilityService.BuildLayerPayload(layer.Resource, layer.LayerIndex))
                .ToArray();
            if (!string.IsNullOrWhiteSpace(filter))
            {
                layerQuery = _querySearchService.ApplyBasicFilter(layerQuery, filter);
            }

            if (!string.IsNullOrWhiteSpace(orderby))
            {
                layerQuery = ApplyLayerPayloadOrdering(layerQuery, orderby);
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

            // Probe one extra layer beyond the requested page so a followable
            // @odata.nextLink is emitted whenever a full page was returned. The Layers
            // collection paginates in memory; without this continuation a client that
            // requests /odata/Layers?$top=N over >N layers concludes there are no more
            // (#1989). $top=0 (Limit == 0) yields an empty page and no continuation.
            var probeLimit = pagination.Limit < int.MaxValue ? pagination.Limit + 1 : pagination.Limit;
            var layerData = layerQuery.Take(probeLimit).ToArray();

            var hasMoreLayers = pagination.Limit > 0 && layerData.Length > pagination.Limit;
            if (hasMoreLayers)
            {
                layerData = layerData[..pagination.Limit];
            }

            // Apply field selection if specified
            object[] result = string.IsNullOrWhiteSpace(select)
                ? layerData.Cast<object>().ToArray()
                : _querySearchService.ApplyFieldSelection(layerData, select);

            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
            var includeContext = ODataUtilityService.ShouldIncludeContext(context.Request, format);

            string? nextLink = null;
            if (hasMoreLayers)
            {
                var nextSkip = ODataUtilityService.CalculateNextSkip(pagination.Offset, pagination.Limit);
                // The Layers collection paginates in memory and decodes an incoming
                // $skiptoken without a filter/orderby fingerprint, so emit a plain $skip
                // continuation (a standard OData system query option) rather than an
                // opaque skiptoken whose fingerprint would not round-trip here.
                nextLink = ODataUtilityService.GenerateNextLink(
                    context.Request,
                    nextSkip,
                    pagination.Limit,
                    filter,
                    select,
                    orderby,
                    countValue,
                    expand: null,
                    useSkipToken: false,
                    compute: null,
                    format: format);
            }

            var response = ODataUtilityService.CreateODataResponse(
                baseUrl,
                "Layers",
                result,
                totalCount,
                nextLink: nextLink,
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
        // Intentional broad catch: request-handling boundary; already logged
        // (Log.LayersQueryFailed) and mapped to an OData-format 500 error.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                requiredProtocol: ODataProtocol,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }

            var layerIndex = layerValidation.Publication?.LayerIndex ?? layerId;
            var payload = ODataUtilityService.BuildLayerPayload(layerValidation.Resource!, layerIndex);
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
        // Intentional broad catch: request-handling boundary; already logged
        // (Log.LayersQueryFailed) and mapped to an OData-format 500 error.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

            IEnumerable<Dictionary<string, object?>> layerQuery = visibleLayers
                .Select(layer => ODataUtilityService.BuildLayerPayload(layer.Resource, layer.LayerIndex))
                .ToArray();
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
        // Intentional broad catch: request-handling boundary; already logged
        // (Log.LayersQueryFailed) and mapped to an OData-format 500 error.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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
        BoundingBox? bbox = null,
        CancellationToken cancellationToken = default)
    {
        // Not converted to a `using` declaration: featureActivity is intentionally created
        // partway through the try block (only once layer validation resolves publicLayerId),
        // and both the catch blocks (RecordException) and the finally need to observe
        // whatever value it holds - including null if validation short-circuited before the
        // activity was started. A using declaration would either force activity creation
        // before validation (polluting telemetry for invalid-layer requests) or be out of
        // scope for the catch blocks.
        // codeql[cs/missed-using-statement] -- lifetime is already managed by explicit cleanup or the owning type.
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
            var layerValidation = await LayerValidationHelpers.ValidateLayerWithAccessV2Async(
                context,
                layerId,
                LayerValidationHelpers.ValidationProtocol.OData,
                requiredProtocol: ODataProtocol,
                cancellationToken: effectiveToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var resource = layerValidation.Resource!;
            var publicLayerId = layerValidation.Publication?.LayerIndex ?? layerId;
            var storageLayerId = ODataV2Lookups.ResolveStorageLayerId(
                layerValidation.Snapshot!,
                layerValidation.Publication,
                resource);
            if (!storageLayerId.HasValue)
            {
                return ODataUtilityService.CreateODataError(
                    context,
                    "InternalServerError",
                    "Layer storage binding is not configured.",
                    StatusCodes.Status500InternalServerError);
            }

            var featureReader = await (_featureProviderResolver
                ?? throw new InvalidOperationException("Feature provider routing is not configured."))
                .ResolveQueryReaderAsync(
                    layerValidation.Snapshot!,
                    layerValidation.Service,
                    resource,
                    layerValidation.Publication,
                    storageLayerId.Value,
                    requireCount: count == true,
                    effectiveToken).ConfigureAwait(false);

            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, publicLayerId.ToString(CultureInfo.InvariantCulture));

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.OData,
                publicLayerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);

            var effectiveFilter = deltaSince.HasValue
                ? ODataDeltaService.BuildDeltaFilter(filter, deltaSince.Value, deltaState?.UpperBoundTimestamp)
                : filter;

            // OData v4 allows $top=0: a deliberately empty page. Short-circuit before
            // building/executing the data query so the empty page never emits an
            // @odata.nextLink, while $count=true still reports the true total via a
            // dedicated count query (independent of the data page). GeoParquet export
            // keeps its own materialization path so the response stays a (zero-row)
            // Parquet payload rather than an empty JSON document.
            if (pagination.Limit == 0 && !ODataUtilityService.IsParquetFormat(format))
            {
                return await HandleEmptyFeaturesPageAsync(
                    context,
                    featureActivity,
                    effectiveFilter,
                    orderby,
                    pagination,
                    resource,
                    select,
                    expand,
                    count,
                    compute,
                    format,
                    bbox,
                    storageLayerId.Value,
                    featureReader,
                    effectiveToken);
            }

            // Build feature query using query service with the clamped page size, then
            // probe one extra row beyond the requested page (Limit + 1) so @odata.nextLink
            // can be decided from whether a full page came back, independent of provider
            // TotalCount fidelity. The optimized Postgres path derives TotalCount from a
            // COUNT(*) OVER() column, but the ExtractTotalCount fallback (and read-only
            // non-Postgres providers) can degrade TotalCount to the page length, which
            // would suppress nextLink and silently truncate paging. The streaming path
            // guards this the same way (#1989).
            //
            // The probe is applied AFTER the build, on the materialized FeatureQuery, rather
            // than by inflating the requested $top: the OData query-parameter adapter clamps
            // $top to OData:MaxPageSize (#1644), so a pre-build Limit + 1 would be clamped
            // straight back to the page size and drop the probe row — truncating paging at
            // the first server page (#1989). pagination.Limit is already clamped to
            // MaxPageSize, so probing the built query keeps the +1 row intact.
            var (featureQuery, queryError) = await _querySearchService.BuildFeatureQueryAsync(
                effectiveFilter,
                orderby,
                pagination.Limit,
                pagination.Offset,
                resource,
                select,
                expand,
                count,
                compute,
                format,
                bbox,
                effectiveToken);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            if (featureQuery.Limit is > 0 and < int.MaxValue)
            {
                featureQuery = featureQuery with { Limit = featureQuery.Limit.Value + 1 };
            }

            var canCache = ResponseCacheUtilities.ShouldCache(context, _cacheOptions) &&
                           string.IsNullOrWhiteSpace(expand) &&
                           string.IsNullOrWhiteSpace(deltatoken) &&
                           !trackChangesRequested &&
                           !ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(featureQuery, filter) &&
                           !AcceptRequestsNonDefaultMetadata(context.Request, format);
            // GetQueryTtlWithJitter() is clamped to >= 1s (CacheOptions.ApplyJitter), so it can
            // never be <= TimeSpan.Zero when canCache is true; a prior "downgrade to no-cache
            // on non-positive TTL" guard here was dead code and has been removed.
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;

            var cacheKey = canCache
                ? ResponseCacheUtilities.BuildODataLayerKey(layerId, context.Request)
                : null;

            if (canCache && cacheKey != null)
            {
                var cached = await _responseCache.GetAsync<CachedResponse>(cacheKey, effectiveToken);
                if (cached != null)
                {
                    ODataUtilityService.SetODataHeaders(context);
                    // codeql[cs/constant-condition] -- the defensive branch preserves compatibility and documents the accepted wire or domain shape.
                    if (trackChangesRequested)
                    {
                        ODataUtilityService.ApplyTrackChangesPreference(context);
                    }

                    HonuaTelemetry.SetSuccess(featureActivity);
                    return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
                }
            }

            // Execute query
            var queryResult = await featureReader.QueryAsync(storageLayerId.Value, featureQuery, effectiveToken);

            // The query fetched up to pagination.Limit + 1 rows as a continuation probe.
            // A returned count exceeding the requested page size means more rows remain,
            // regardless of the provider-reported TotalCount. Trim the probe row off the
            // materialized page so the response never returns more than the requested $top.
            var hasMoreResults = queryResult.Items.Length > pagination.Limit;
            if (hasMoreResults)
            {
                queryResult = queryResult with { Items = queryResult.Items[..pagination.Limit] };
            }

            // GeoParquet export ($format=parquet): emit the materialized page through the
            // shared cloud-native writer so OData reaches parity with the GeoServices
            // f=parquet surface without a protocol-local Parquet path.
            if (ODataUtilityService.IsParquetFormat(format))
            {
                HonuaTelemetry.SetSuccess(featureActivity, queryResult.Items.Length);
                return WriteGeoParquetResult(context, resource, queryResult, select);
            }

            // Process $expand for related entities
            Dictionary<long, Dictionary<string, object?[]>>? expandedRelations = null;
            if (!string.IsNullOrWhiteSpace(expand))
            {
                expandedRelations = await _querySearchService.ProcessExpandAsync(
                    expand,
                    resource,
                    storageLayerId.Value,
                    queryResult.Items.Select(f => f.Id).ToArray(),
                    context,
                    effectiveToken);
            }

            var layerSrid = resource.ReadSrid() ?? 4326;
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
                var dict = ODataUtilityService.BuildFeaturePayload(publicLayerId, f, geometry, attributes);

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

            // Calculate @odata.nextLink if there are more results. The continuation probe
            // (Limit + 1 fetch) is the source of truth here so paging is not truncated when
            // the provider TotalCount degrades to the page length (#1989).
            string? nextLink = null;
            if (hasMoreResults)
            {
                var nextSkip = ODataUtilityService.CalculateNextSkip(pagination.Offset, pagination.Limit);
                nextLink = !string.IsNullOrWhiteSpace(deltatoken)
                    ? ODataUtilityService.GenerateDeltaNextLink(
                        context.Request,
                        deltaState ?? throw new InvalidOperationException("Delta state is required for delta paging."),
                        nextSkip,
                        pagination.Limit,
                        useSkipToken,
                        deltaState.Filter,
                        deltaState.OrderBy)
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
                    LayerId = publicLayerId,
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
                        LayerId = publicLayerId,
                        Count = count
                    }
                    : baseDeltaState with
                    {
                        LayerId = publicLayerId,
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
        // Intentional broad catch: request-handling boundary; already logged
        // (Log.FeaturesQueryFailed) and mapped to an OData-format 500 error.
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

    /// <summary>
    /// Handles an OData <c>$top=0</c> request: returns a 200 with an empty <c>value</c>
    /// array and no <c>@odata.nextLink</c>. When <c>$count=true</c> the true total is
    /// computed with a dedicated count query so the empty page still reports the count,
    /// independent of any data-page TotalCount fidelity.
    /// </summary>
    private async Task<IResult> HandleEmptyFeaturesPageAsync(
        HttpContext context,
        Activity? featureActivity,
        string? filter,
        string? orderby,
        Honua.Core.Features.Validation.PaginationValues pagination,
        MetadataV2Resource resource,
        string? select,
        string? expand,
        bool? count,
        string? compute,
        string? format,
        BoundingBox? bbox,
        int storageLayerId,
        IFeatureReader featureReader,
        CancellationToken cancellationToken)
    {
        long? totalCount = null;
        if (count == true)
        {
            // The count is the true total of the filtered set, independent of the empty
            // ($top=0) data page. Build the count query with a null limit rather than the
            // page's Limit of 0: the shared query validator rejects Limit <= 0 ("Limit must
            // be greater than zero"), so passing 0 here would turn $top=0&$count=true into a
            // 400 (#1989). Offset is likewise irrelevant to the total, so it is dropped.
            var (countQuery, queryError) = await _querySearchService.BuildFeatureQueryAsync(
                filter,
                orderby,
                resultRecordCount: null,
                resultOffset: null,
                resource,
                select,
                expand,
                count,
                compute,
                format,
                bbox,
                cancellationToken);

            if (queryError != null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidQuery", queryError);
            }

            totalCount = await featureReader.CountAsync(storageLayerId, countQuery, cancellationToken);
        }

        var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);
        var response = new ODataResponse
        {
            Context = ODataUtilityService.ShouldIncludeContext(context.Request, format)
                ? ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: select, expand: expand)
                : null,
            Count = totalCount,
            NextLink = null,
            DeltaLink = null,
            Value = Array.Empty<object>()
        };

        ODataUtilityService.SetODataHeaders(context);
        HonuaTelemetry.SetSuccess(featureActivity, 0);
        return Results.Json(response, ODataJsonContext.Default.ODataResponse,
            contentType: ODataUtilityService.GetODataContentType(context.Request, format));
    }

    /// <summary>
    /// Writes a materialized query result as GeoParquet using the shared cloud-native writer.
    /// </summary>
    private IResult WriteGeoParquetResult(
        HttpContext context,
        MetadataV2Resource resource,
        QueryResult<Feature> queryResult,
        string? select)
    {
        var limitsOptions = context.RequestServices.GetService<IOptions<LimitsOptions>>();
        var geometryLimits = limitsOptions?.Value?.Geometry ?? new GeometryLimits();
        var objectIdFieldName = resource.FindPrimaryIdField()?.Name ?? FieldNames.ObjectId;
        var selectedFields = ODataUtilityService.ParseSelect(select);
        var outFields = selectedFields is { Count: > 0 } ? selectedFields.ToArray() : null;
        var layerSrid = resource.ReadSrid() ?? 4326;

        try
        {
            var (payload, contentType) = GeoParquetFeatureWriter.FormatAsGeoParquet(
                queryResult,
                resource,
                objectIdFieldName,
                returnGeometry: true,
                outputSrid: layerSrid,
                returnZ: false,
                returnM: false,
                geometryLimits,
                outFields,
                _logger);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Bytes(payload, contentType);
        }
        catch (ParquetRuntimeUnavailableException)
        {
            // $format=parquet requested on a runtime image that cannot load the native
            // ParquetSharp Arrow encoder (glibc-only; absent on Alpine/musl). Return a
            // clean 501 capability response instead of an unhandled 500 (honua-server#1942).
            return ODataUtilityService.CreateODataError(
                context,
                "NotImplemented",
                ParquetRuntimeUnavailableException.CapabilityMessage,
                StatusCodes.Status501NotImplemented);
        }
        catch (InvalidOperationException ex)
        {
            // The shared writer rejects unsupported cloud-native geometry exports
            // (non-4326 SRID, returnM). Surface as a 400 rather than a 500.
            return ODataUtilityService.CreateODataError(context, "InvalidQueryOption", ex.Message);
        }
    }

    private static async Task<ODataV2Lookups.ResolvedODataPublication[]> GetVisibleODataLayersAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask<AxisOrder> ResolveAxisOrderAsync(int? srid, CancellationToken cancellationToken)
    {
        return ODataCrsUtilities.ResolveAxisOrderAsync(_crsRegistry, srid, cancellationToken);
    }

    private static IEnumerable<Dictionary<string, object?>> ApplyLayerPayloadOrdering(
        IEnumerable<Dictionary<string, object?>> layers,
        string orderby)
    {
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;

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

            ordered = ApplyLayerPayloadOrderingClause(ordered, layers, field, ascending);
        }

        return ordered ?? layers;
    }

    private static IOrderedEnumerable<Dictionary<string, object?>> ApplyLayerPayloadOrderingClause(
        IOrderedEnumerable<Dictionary<string, object?>>? ordered,
        IEnumerable<Dictionary<string, object?>> layers,
        string field,
        bool ascending)
    {
        return field.ToLowerInvariant() switch
        {
            "id" => ApplyOrder(ordered, layers, static layer => Convert.ToInt32(layer["Id"], CultureInfo.InvariantCulture), ascending),
            "name" => ApplyOrder(ordered, layers, static layer => Convert.ToString(layer["Name"], CultureInfo.InvariantCulture) ?? string.Empty, ascending),
            "description" => ApplyOrder(ordered, layers, static layer => Convert.ToString(layer["Description"], CultureInfo.InvariantCulture) ?? string.Empty, ascending),
            "geometrytype" => ApplyOrder(ordered, layers, static layer => Convert.ToString(layer["GeometryType"], CultureInfo.InvariantCulture) ?? string.Empty, ascending),
            "srid" => ApplyOrder(ordered, layers, static layer => Convert.ToInt32(layer["Srid"], CultureInfo.InvariantCulture), ascending),
            _ => throw new ArgumentException($"Unknown field in $orderby: {field}")
        };
    }

    private static IOrderedEnumerable<Dictionary<string, object?>> ApplyOrder<TKey>(
        IOrderedEnumerable<Dictionary<string, object?>>? ordered,
        IEnumerable<Dictionary<string, object?>> layers,
        Func<Dictionary<string, object?>, TKey> selector,
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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.GeoServices;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Parsing;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Handler for FeatureServer query operations.
/// </summary>
internal sealed class FeatureServerQueryHandler(
    FeatureServerQueryDependencies dependencies,
    ILogger<FeatureServerQueryHandler> logger) : IFeatureQueryDispatcher
{
    private static readonly IResult _streamingResult = new StreamingResult();
    private static readonly Regex _projectedUnitFactorPattern = new(
        @"(?:LENGTHUNIT|UNIT)\s*\[\s*""[^""]+""\s*,\s*(?<factor>[-+]?(?:\d+\.?\d*|\.\d+)(?:[Ee][-+]?\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureServerQueryServices _queryServices = dependencies.QueryServices;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly FeatureServerQueryExecutor _queryExecutor = dependencies.QueryExecutor;
    private readonly IQueryParameterAdapter<GeoServicesQueryRequest> _queryParameterAdapter = dependencies.QueryParameterAdapter;
    private readonly IQueryProcessor _queryProcessor = dependencies.QueryProcessor;
    private readonly IResponseCache _responseCache = dependencies.ResponseCache;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly ILogger<FeatureServerQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int StreamingThreshold = 1000;
    private const string InvalidTimeParameterMessage = "Invalid time parameter.";
    private const string InvalidOutStatisticsJsonMessage = "outStatistics must be a valid JSON array.";

    /// <summary>
    /// Executes a feature query operation with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        CancellationToken cancellationToken = default)
        => await HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            values,
            context,
            queryValidator,
            requiredProtocol: null,
            cancellationToken)
            .ConfigureAwait(false);

    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        string? requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        if (!FeatureServerEndpoints.TryParseQueryParameters(values, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid query parameter."]);
        }

        return await HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            requiredProtocol,
            cancellationToken);
    }

    /// <summary>
    /// Executes a feature query operation with parsed query parameters.
    /// </summary>
    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        CancellationToken cancellationToken = default)
        => await HandleQueryFeaturesAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            requiredProtocol: null,
            cancellationToken)
            .ConfigureAwait(false);

    public async Task<(QueryResponse? Response, IResult? Error)> HandleServiceQueryLayerAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;
        try
        {
            var hasWhereClause = !string.IsNullOrWhiteSpace(queryParams.Where);
            FeatureServerLog.QueryRequested(
                _logger,
                serviceId,
                layerId,
                hasWhereClause);

            var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
                _resourceValidator,
                serviceId,
                layerId,
                context,
                _logger,
                requiredProtocol ?? ServiceProtocols.FeatureServer,
                cancellationToken).ConfigureAwait(false);
            if (!resourceValidationResult.IsValid)
            {
                return (null, resourceValidationResult.ErrorResult!);
            }

            ServiceDefinition service = resourceValidationResult.Service!;
            LayerDefinition layer = resourceValidationResult.Layer!;
            var telemetryProtocol = ResolveTelemetryProtocol(requiredProtocol);
            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, telemetryProtocol);
            requestActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
            if (accessError != null)
            {
                return (null, accessError);
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                telemetryProtocol,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);
            featureActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

            var whereValidation = queryValidator.ValidateWhereClause(queryParams.Where);
            if (!whereValidation.IsValid)
            {
                var message = whereValidation.ErrorMessage ?? ErrorMessages.Validation.InvalidParameter;
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [message]));
            }

            var queryLimits = queryValidator.QueryLimits;

            var queryValidationResult = _queryServices.ValidateQueryLimits(queryParams);
            if (!queryValidationResult.IsValid)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Query parameters exceed configured limits",
                    [queryValidationResult.ErrorMessage!]));
            }

            QueryParameters validatedParams = queryValidationResult.ValidatedParameters!;

            if (!TryValidateUnsupportedParameters(validatedParams, out var unsupportedError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Unsupported query parameters",
                    [unsupportedError!]));
            }

            var requestedFormat = validatedParams.FormatSpecified ? validatedParams.F : "json";
            if (!FeatureServerEndpoints.TryValidateOutputFormat(
                requestedFormat,
                FeatureServerEndpoints.JsonOnlyFormats,
                out _,
                out var formatError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    [formatError ?? "Output format is not supported."]));
            }

            var (query, outputSrid, preparationError) = await PrepareFeatureQueryAsync(
                context,
                layer,
                validatedParams,
                queryLimits,
                "json",
                cancellationToken).ConfigureAwait(false);
            if (preparationError != null)
            {
                return (null, preparationError);
            }

            if (!query.HasValue)
            {
                return (null, StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed"));
            }

            var response = await ExecuteJsonQueryResponseAsync(
                serviceId,
                layerId,
                layer,
                validatedParams,
                query.Value,
                outputSrid,
                context,
                cancellationToken).ConfigureAwait(false);

            HonuaTelemetry.SetSuccess(featureActivity, CountResponseItems(response));
            return (response, null);
        }
        catch (ArgumentException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            return (null, StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter));
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            return IsClientSafeInvalidOperation(ex)
                ? (null, StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter))
                : (null, StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed"));
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return (null, StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed"));
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        string? requiredProtocol,
        CancellationToken cancellationToken = default)
    {
        Activity? featureActivity = null;
        try
        {
            var hasWhereClause = !string.IsNullOrWhiteSpace(queryParams.Where);
            FeatureServerLog.QueryRequested(
                _logger,
                serviceId,
                layerId,
                hasWhereClause);

            var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
                _resourceValidator,
                serviceId,
                layerId,
                context,
                _logger,
                requiredProtocol ?? ServiceProtocols.FeatureServer,
                cancellationToken);
            if (!resourceValidationResult.IsValid)
            {
                return resourceValidationResult.ErrorResult!;
            }

            ServiceDefinition service = resourceValidationResult.Service!;
            LayerDefinition layer = resourceValidationResult.Layer!;
            var telemetryProtocol = ResolveTelemetryProtocol(requiredProtocol);
            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, telemetryProtocol);
            requestActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
            if (accessError != null)
            {
                return accessError;
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                telemetryProtocol,
                layerId.ToString(CultureInfo.InvariantCulture),
                context.TraceIdentifier);
            featureActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);

            var whereValidation = queryValidator.ValidateWhereClause(queryParams.Where);
            if (!whereValidation.IsValid)
            {
                var message = whereValidation.ErrorMessage ?? ErrorMessages.Validation.InvalidParameter;
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [message]);
            }

            var queryLimits = queryValidator.QueryLimits;

            // Apply limits enforcement
            var queryValidationResult = _queryServices.ValidateQueryLimits(queryParams);
            if (!queryValidationResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Query parameters exceed configured limits",
                    [queryValidationResult.ErrorMessage!]);
            }

            QueryParameters validatedParams = queryValidationResult.ValidatedParameters!;

            if (!TryValidateUnsupportedParameters(validatedParams, out var unsupportedError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Unsupported query parameters",
                    [unsupportedError!]);
            }

            var requestedFormat = FeatureServerEndpoints.ResolveRequestedQueryFormat(
                validatedParams,
                context.Request.Headers.Accept);

            if (!FeatureServerEndpoints.TryValidateOutputFormat(
                requestedFormat,
                FeatureServerEndpoints.FeatureServerQueryFormats,
                out var format,
                out var formatError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    [formatError ?? "Output format is not supported."]);
            }

            if (string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase) && validatedParams.ReturnM)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    ["GeoJSON output does not support returnM=true."]);
            }

            var canCache = ResponseCacheUtilities.ShouldCache(context, _cacheOptions);
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;
            if (canCache && cacheTtl <= TimeSpan.Zero)
            {
                canCache = false;
            }

            var cacheKey = canCache
                ? ResponseCacheUtilities.BuildFeatureServerKey(serviceId, layerId, context.Request)
                : null;

            async Task<IResult?> TryGetCachedResponseAsync()
            {
                if (!canCache || cacheKey is null)
                {
                    return null;
                }

                var cached = await _responseCache.GetAsync<CachedResponse>(cacheKey, cancellationToken);
                return cached == null
                    ? null
                    : ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
            }

            async Task<IResult> CreateCachedResultAsync<T>(
                T response,
                JsonTypeInfo<T> typeInfo,
                string contentType)
            {
                if (!canCache || cacheKey is null)
                {
                    return Results.Json(response, typeInfo, contentType: contentType);
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(response, typeInfo);
                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            async Task<IResult> CreateCachedBytesResultAsync(byte[] payload, string contentType)
            {
                if (!canCache || cacheKey is null)
                {
                    return Results.Bytes(payload, contentType);
                }

                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(cacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            var (preparedQuery, outputSrid, preparationError) = await PrepareFeatureQueryAsync(
                context,
                layer,
                validatedParams,
                queryLimits,
                format,
                cancellationToken).ConfigureAwait(false);
            if (preparationError != null)
            {
                return preparationError;
            }

            if (!preparedQuery.HasValue)
            {
                return StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed");
            }

            var query = preparedQuery.Value;

            // Handle statistics queries (outStatistics)
            if (!string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
            {
                if (!TryParseStatisticsDefinitions(validatedParams.OutStatistics, layer, out var statisticsDefs, out var statsError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid outStatistics",
                        [statsError ?? InvalidOutStatisticsJsonMessage]);
                }

                ImmutableArray<string>? groupByFields = null;
                if (!string.IsNullOrWhiteSpace(validatedParams.GroupByFieldsForStatistics))
                {
                    var parsed = validatedParams.GroupByFieldsForStatistics
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim())
                        .ToImmutableArray();
                    if (parsed.IsDefaultOrEmpty)
                    {
                        return StandardErrorHelpers.CreateBadRequest(context,
                            "Invalid groupByFieldsForStatistics",
                            ["groupByFieldsForStatistics must contain valid field names."]);
                    }

                    groupByFields = parsed;
                }

                var statisticsQuery = query with
                {
                    OutStatistics = statisticsDefs,
                    GroupByFields = groupByFields,
                    Limit = null,
                    Offset = null,
                    OrderBy = null,
                    Distinct = false
                };

                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var stopwatch = Stopwatch.StartNew();
                var statisticsRows = await _queryExecutor.QueryStatisticsAsync(layerId, statisticsQuery, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "statistics", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

                var statisticsFeatures = statisticsRows.Select(row => new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase),
                    Geometry = null
                }).ToArray();

                HonuaTelemetry.SetSuccess(featureActivity, statisticsFeatures.Length);
                var statisticsResponse = new QueryResponse { Features = statisticsFeatures };
                return await CreateCachedResultAsync(statisticsResponse, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            var objectIdFieldName = layer.ObjectIdFieldName;

            if (validatedParams.ReturnCountOnly)
            {
                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var stopwatch = Stopwatch.StartNew();
                var count = await _queryExecutor.CountAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "count", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                var safeCount = (int)Math.Min(count, int.MaxValue);
                HonuaTelemetry.SetSuccess(featureActivity, safeCount);
                var response = new QueryResponse
                {
                    Count = count,
                    Features = null
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            if (validatedParams.ReturnExtentOnly)
            {
                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var stopwatch = Stopwatch.StartNew();
                var extent = await _queryExecutor.GetExtentAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "extent", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                extent ??= await ResolveExtentFallbackAsync(context, validatedParams, layer, outputSrid, cancellationToken);
                HonuaTelemetry.SetSuccess(featureActivity);
                var response = new QueryResponse
                {
                    Extent = extent.HasValue ? extent.Value.ToExtentInfo() : null,
                    Features = null
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            if (validatedParams.ReturnIdsOnly)
            {
                var idsEffectiveLimit = query.Limit ?? validatedParams.ObjectIds?.Length ?? queryLimits.DefaultRecordCount;
                var idsUseStreaming = idsEffectiveLimit > StreamingThreshold;
                if (idsUseStreaming)
                {
                    await _queryExecutor.StreamIdsAsync(
                        layerId,
                        query,
                        objectIdFieldName,
                        context,
                        cancellationToken);
                    return _streamingResult;
                }

                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var stopwatch = Stopwatch.StartNew();
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "ids", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

                var objectIds = result.Items.Select(feature => feature.Id).ToArray();
                HonuaTelemetry.SetSuccess(featureActivity, objectIds.Length);

                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    ObjectIds = objectIds,
                    Features = null
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            var effectiveLimit = query.Limit ?? validatedParams.ObjectIds?.Length ?? queryLimits.DefaultRecordCount;
            var isPbf = string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase);
            var isFgb = string.Equals(format, "fgb", StringComparison.OrdinalIgnoreCase);
            var isGeobuf = string.Equals(format, "geobuf", StringComparison.OrdinalIgnoreCase);
            var isParquet = string.Equals(format, "parquet", StringComparison.OrdinalIgnoreCase);
            var isArrow = string.Equals(format, "arrow", StringComparison.OrdinalIgnoreCase);
            var useStreaming = effectiveLimit > StreamingThreshold && !isPbf && !isFgb && !isGeobuf && !isParquet && !isArrow;

            if (!useStreaming)
            {
                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                if (isFgb)
                {
                    if (validatedParams.ReturnDistinctValues)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Unsupported query parameters",
                            ["returnDistinctValues is not supported when f=fgb."]);
                    }

                    var fgbStopwatch = Stopwatch.StartNew();
                    var flatGeobufPayload = await _queryExecutor.QueryFlatGeobufWithValidationAsync(layerId, query, cancellationToken);
                    fgbStopwatch.Stop();
                    FeatureServerLog.QueryExecuted(_logger, "query_fgb", serviceId, layerId, fgbStopwatch.Elapsed.TotalMilliseconds);

                    var payload = flatGeobufPayload ?? [];
                    FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, payload.Length > 0 ? 1 : 0, payload.Length > 0 ? 1 : 0);
                    HonuaTelemetry.SetSuccess(featureActivity, payload.Length > 0 ? 1 : 0);

                    return await CreateCachedBytesResultAsync(payload, "application/vnd.flatgeobuf");
                }

                if (isGeobuf)
                {
                    if (validatedParams.ReturnDistinctValues)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Unsupported query parameters",
                            ["returnDistinctValues is not supported when f=geobuf."]);
                    }

                    if (!_queryExecutor.SupportsGeobufOutput)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Unsupported output format",
                            ["Output format 'geobuf' is not supported by the configured feature store."]);
                    }

                    var geobufStopwatch = Stopwatch.StartNew();
                    var geobufPayload = await _queryExecutor.QueryGeobufWithValidationAsync(layerId, query, cancellationToken);
                    geobufStopwatch.Stop();
                    FeatureServerLog.QueryExecuted(_logger, "query_geobuf", serviceId, layerId, geobufStopwatch.Elapsed.TotalMilliseconds);

                    var payload = geobufPayload ?? [];
                    FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, payload.Length > 0 ? 1 : 0, payload.Length > 0 ? 1 : 0);
                    HonuaTelemetry.SetSuccess(featureActivity, payload.Length > 0 ? 1 : 0);

                    return await CreateCachedBytesResultAsync(payload, "application/geobuf");
                }

                var queryStopwatch = Stopwatch.StartNew();
                string[]? outFields;
                if (string.IsNullOrEmpty(validatedParams.OutFields))
                {
                    outFields = null;
                }
                else
                {
                    var parsed = validatedParams.OutFields
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(f => f.Trim())
                        .Where(f => f.Length > 0)
                        .ToArray();
                    // An input that collapses to an empty array after parsing (e.g. "outFields=,,")
                    // must be treated as "all fields" per Esri semantics, NOT as "no fields".
                    outFields = parsed.Length == 0 ? null : parsed;
                }
                var shouldApplyDistinct = validatedParams.ReturnDistinctValues && outFields is { Length: > 0 };
                var queryForExecution = shouldApplyDistinct
                    ? query with { Limit = null, Offset = null }
                    : query;
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(layerId, queryForExecution, cancellationToken);
                queryStopwatch.Stop();
                var queryOperation = isParquet ? "query_parquet" : isArrow ? "query_arrow" : "query";
                FeatureServerLog.QueryExecuted(_logger, queryOperation, serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

                if (shouldApplyDistinct)
                {
                    result = ApplyDistinctValues(result, outFields!);
                    result = ApplyPaginationWindow(result, query.Offset, query.Limit);
                }

                (object? formattedResponse, string? contentType) = await _queryServices.FormatQueryResultAsync(
                    result,
                    layer,
                    format,
                    validatedParams.ReturnGeometry,
                    outputSrid,
                    validatedParams.ReturnZ,
                    validatedParams.ReturnM,
                    validatedParams.GeometryPrecision,
                    validatedParams.MaxAllowableOffset,
                    outFields);

                FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, result.Items.Length, result.TotalCount);
                HonuaTelemetry.SetSuccess(featureActivity, result.Items.Length);

                if (isPbf)
                {
                    return await CreateCachedBytesResultAsync(
                        (byte[])formattedResponse!,
                        contentType ?? "application/x-protobuf");
                }

                if (isParquet)
                {
                    return await CreateCachedBytesResultAsync(
                        (byte[])formattedResponse!,
                        contentType ?? "application/vnd.apache.parquet");
                }

                if (isArrow)
                {
                    return await CreateCachedBytesResultAsync(
                        (byte[])formattedResponse!,
                        contentType ?? "application/vnd.apache.arrow.stream");
                }

                return format.ToLowerInvariant() switch
                {
                    "geojson" => await CreateCachedResultAsync(
                        (GeoJsonFeatureSet)formattedResponse!,
                        FeatureServerJsonContext.Default.GeoJsonFeatureSet,
                        contentType ?? "application/geo+json"),
                    _ => await CreateCachedResultAsync(
                        (QueryResponse)formattedResponse!,
                        FeatureServerJsonContext.Default.QueryResponse,
                        contentType ?? "application/json")
                };
            }

            await _queryExecutor.StreamQueryAsync(
                layerId,
                query,
                layer,
                validatedParams,
                outputSrid,
                context,
                cancellationToken);

            HonuaTelemetry.SetSuccess(featureActivity);
            return _streamingResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Honua.Core.Exceptions.ResourceNotFoundException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateFromException(context, ex);
        }
        catch (Honua.Core.Exceptions.ValidationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateFromException(context, ex);
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            if (IsClientSafeInvalidOperation(ex))
            {
                return StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter);
            }

            return StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed");
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed");
        }
        finally
        {
            featureActivity?.Dispose();
        }
    }

    private async Task<(FeatureQuery? Query, int? OutputSrid, IResult? Error)> PrepareFeatureQueryAsync(
        HttpContext context,
        LayerDefinition layer,
        QueryParameters validatedParams,
        QueryLimits queryLimits,
        string format,
        CancellationToken cancellationToken)
    {
        GeoServicesGeometry? parsedGeometry = null;
        if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(validatedParams.Geometry, validatedParams.GeometryType, out parsedGeometry, out var geometryError))
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                ErrorMessages.Validation.InvalidGeometryParameter,
                [geometryError ?? "Geometry parameter is invalid."]));
        }

        var inputSrid = await _queryServices.ResolveSridAsync(validatedParams.InSr, parsedGeometry?.SpatialReference, cancellationToken).ConfigureAwait(false);
        if (validatedParams.InSrSpecified && !inputSrid.HasValue)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid input spatial reference",
                [CreateSpatialReferenceErrorMessage("inSR", validatedParams.InSr)]));
        }

        if (parsedGeometry != null && !inputSrid.HasValue)
        {
            inputSrid = layer.SpatialReference.ToSrid();
        }

        if (parsedGeometry != null && inputSrid.HasValue)
        {
            var queryGeometrySpatialReference = ResolveAreaLimitSpatialReference(layer, parsedGeometry, inputSrid.Value);

            var geometryValidationResult = ValidateGeometryCoordinates(parsedGeometry, queryGeometrySpatialReference);
            if (!geometryValidationResult.IsValid)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidGeometryParameter,
                    [geometryValidationResult.ErrorMessage!]));
            }

            if (queryLimits.MaxBboxAreaSqKm > 0)
            {
                var areaLimitResult = ValidateBboxAreaLimit(parsedGeometry, queryGeometrySpatialReference, queryLimits.MaxBboxAreaSqKm);
                if (!areaLimitResult.IsValid)
                {
                    return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                        "Query parameters exceed configured limits",
                        [areaLimitResult.ErrorMessage!]));
                }
            }
        }

        var outputSrid = await _queryServices.ResolveSridAsync(validatedParams.OutSr, null, cancellationToken).ConfigureAwait(false);
        if (validatedParams.OutSrSpecified && !outputSrid.HasValue)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid output spatial reference",
                [CreateSpatialReferenceErrorMessage("outSR", validatedParams.OutSr)]));
        }

        var wgs84Srid = SpatialReference.WGS84.Wkid;
        var requiresGeoJsonOutput = string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase)
            && !validatedParams.ReturnCountOnly
            && !validatedParams.ReturnExtentOnly
            && !validatedParams.ReturnIdsOnly;

        if (requiresGeoJsonOutput)
        {
            if (outputSrid.HasValue && outputSrid.Value != wgs84Srid)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    "GeoJSON output only supports EPSG:4326 (WGS84)."));
            }

            outputSrid ??= wgs84Srid;
        }

        var isCloudNativeFormat = string.Equals(format, "parquet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, "arrow", StringComparison.OrdinalIgnoreCase);
        var requiresCloudNativeGeometry = isCloudNativeFormat
            && !validatedParams.ReturnCountOnly
            && !validatedParams.ReturnExtentOnly
            && !validatedParams.ReturnIdsOnly
            && validatedParams.ReturnGeometry
            && layer.HasGeometry
            && string.IsNullOrWhiteSpace(validatedParams.OutStatistics);
        if (requiresCloudNativeGeometry)
        {
            var formatLabel = string.Equals(format, "parquet", StringComparison.OrdinalIgnoreCase)
                ? "GeoParquet"
                : "GeoArrow";

            if (validatedParams.ReturnM)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"{formatLabel} output does not support returnM=true. GeoParquet 1.1.0 only supports XY and XYZ geometries."));
            }

            if (outputSrid.HasValue && outputSrid.Value != wgs84Srid)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    $"{formatLabel} output does not yet support non-4326 outSR. CRS metadata cannot be written correctly for the requested spatial reference."));
            }

            outputSrid ??= wgs84Srid;
        }

        FilterExpression? filterExpression = null;
        if (!string.IsNullOrWhiteSpace(validatedParams.Where))
        {
            var parseResult = _filterExpressionService.Parse(FilterLanguage.ArcGisSql, validatedParams.Where);
            if (!parseResult.IsSuccess)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [parseResult.ErrorMessage ?? "Invalid filter syntax."]));
            }

            filterExpression = parseResult.Expression;
            if (filterExpression != null && !FilterExpressionHelpers.IsBooleanFilterExpression(filterExpression))
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    ["Invalid where clause."]));
            }
        }

        FilterExpression? temporalExpression = null;
        if (!string.IsNullOrWhiteSpace(validatedParams.Time))
        {
            try
            {
                temporalExpression = GeoServicesTemporalQueryBuilder.BuildTemporalExpression(
                    validatedParams.Time,
                    validatedParams.TimeRelation,
                    layer);
            }
            catch (ArgumentException)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [InvalidTimeParameterMessage]));
            }
        }

        if (filterExpression != null && temporalExpression != null)
        {
            filterExpression = new BinaryExpression(filterExpression, BinaryOperator.And, temporalExpression);
        }
        else
        {
            filterExpression ??= temporalExpression;
        }

        SqlFragment? sqlFilter = null;
        if (filterExpression != null)
        {
            var translationResult = _filterExpressionService.Translate(filterExpression, layer);
            if (!translationResult.IsSuccess)
            {
                return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [translationResult.ErrorMessage ?? "Invalid filter syntax."]));
            }

            sqlFilter = translationResult.SqlFilter;
        }

        var queryAdapterResult = await _queryParameterAdapter.ConvertAsync(
            new GeoServicesQueryRequest
            {
                Parameters = validatedParams,
                ParsedGeometry = parsedGeometry,
                InputSrid = inputSrid,
                OutputSrid = outputSrid,
                QueryLimits = queryLimits,
                SqlFilter = sqlFilter
            },
            layer,
            cancellationToken).ConfigureAwait(false);
        if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [queryAdapterResult.ErrorMessage ?? "Invalid query parameters."]));
        }

        var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, layer);
        var unifiedQueryValidation = _queryProcessor.ValidateQuery(unifiedQuery, layer);
        if (!unifiedQueryValidation.IsValid)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [unifiedQueryValidation.ErrorMessage ?? "Invalid query parameters."]));
        }

        var query = _queryProcessor.ToFeatureQuery(unifiedQuery, layer) with
        {
            Where = validatedParams.Where
        };
        return (query, outputSrid, null);
    }

    private async Task<QueryResponse> ExecuteJsonQueryResponseAsync(
        string serviceId,
        int layerId,
        LayerDefinition layer,
        QueryParameters validatedParams,
        FeatureQuery query,
        int? outputSrid,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
        {
            if (!TryParseStatisticsDefinitions(validatedParams.OutStatistics, layer, out var statisticsDefs, out var statsError))
            {
                throw new ArgumentException(statsError ?? InvalidOutStatisticsJsonMessage);
            }

            ImmutableArray<string>? groupByFields = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.GroupByFieldsForStatistics))
            {
                var parsed = validatedParams.GroupByFieldsForStatistics
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToImmutableArray();
                if (parsed.IsDefaultOrEmpty)
                {
                    throw new ArgumentException("groupByFieldsForStatistics must contain valid field names.");
                }

                groupByFields = parsed;
            }

            var statisticsQuery = query with
            {
                OutStatistics = statisticsDefs,
                GroupByFields = groupByFields,
                Limit = null,
                Offset = null,
                OrderBy = null,
                Distinct = false
            };

            var stopwatch = Stopwatch.StartNew();
            var statisticsRows = await _queryExecutor.QueryStatisticsAsync(layerId, statisticsQuery, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "statistics", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

            var statisticsFeatures = statisticsRows.Select(row => new GeoServicesFeature
            {
                Attributes = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase),
                Geometry = null
            }).ToArray();

            return new QueryResponse { Features = statisticsFeatures };
        }

        var objectIdFieldName = layer.ObjectIdFieldName;

        if (validatedParams.ReturnCountOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            var count = await _queryExecutor.CountAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "count", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
            return new QueryResponse
            {
                Count = count,
                Features = null
            };
        }

        if (validatedParams.ReturnExtentOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            var extent = await _queryExecutor.GetExtentAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "extent", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
            extent ??= await ResolveExtentFallbackAsync(context, validatedParams, layer, outputSrid, cancellationToken).ConfigureAwait(false);
            return new QueryResponse
            {
                Extent = extent.HasValue ? extent.Value.ToExtentInfo() : null,
                Features = null
            };
        }

        if (validatedParams.ReturnIdsOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "ids", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

            return new QueryResponse
            {
                ObjectIdFieldName = objectIdFieldName,
                ObjectIds = result.Items.Select(feature => feature.Id).ToArray(),
                Features = null
            };
        }

        string[]? outFields;
        if (string.IsNullOrEmpty(validatedParams.OutFields))
        {
            outFields = null;
        }
        else
        {
            var parsed = validatedParams.OutFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .ToArray();
            outFields = parsed.Length == 0 ? null : parsed;
        }

        var shouldApplyDistinct = validatedParams.ReturnDistinctValues && outFields is { Length: > 0 };
        var queryForExecution = shouldApplyDistinct
            ? query with { Limit = null, Offset = null }
            : query;
        var queryStopwatch = Stopwatch.StartNew();
        QueryResult<Feature> queryResult = await _queryExecutor.QueryWithValidationAsync(layerId, queryForExecution, cancellationToken).ConfigureAwait(false);
        queryStopwatch.Stop();
        FeatureServerLog.QueryExecuted(_logger, "query", serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

        if (shouldApplyDistinct)
        {
            queryResult = ApplyDistinctValues(queryResult, outFields!);
            queryResult = ApplyPaginationWindow(queryResult, query.Offset, query.Limit);
        }

        (object? formattedResponse, _) = await _queryServices.FormatQueryResultAsync(
            queryResult,
            layer,
            "json",
            validatedParams.ReturnGeometry,
            outputSrid,
            validatedParams.ReturnZ,
            validatedParams.ReturnM,
            validatedParams.GeometryPrecision,
            validatedParams.MaxAllowableOffset,
            outFields).ConfigureAwait(false);

        var response = (QueryResponse)formattedResponse!;
        FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, queryResult.Items.Length, queryResult.TotalCount);
        return response;
    }

    private static int CountResponseItems(QueryResponse response)
        => response.Features?.Length
           ?? response.ObjectIds?.Length
           ?? (int)Math.Min(response.Count ?? 0, int.MaxValue);

    private sealed class StreamingResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    internal static async ValueTask<FeatureExtent?> ResolveExtentFallbackAsync(
        HttpContext context,
        QueryParameters queryParams,
        LayerDefinition layer,
        int? outputSrid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queryParams);

        if (!CanFallbackToLayerExtent(queryParams) || !layer.Extent.HasValue)
        {
            return null;
        }

        var layerExtent = layer.Extent.Value;
        if (!outputSrid.HasValue || outputSrid.Value == layerExtent.SpatialReference)
        {
            return layerExtent;
        }

        var transformService = context.RequestServices.GetService<ICoordinateTransformService>();
        if (transformService is null)
        {
            return null;
        }

        var transformedExtent = await transformService.TransformExtentAsync(
            layerExtent.MinX,
            layerExtent.MinY,
            layerExtent.MaxX,
            layerExtent.MaxY,
            layerExtent.SpatialReference,
            outputSrid.Value,
            cancellationToken);

        return transformedExtent.HasValue
            ? FeatureExtent.Create(
                transformedExtent.Value.MinX,
                transformedExtent.Value.MinY,
                transformedExtent.Value.MaxX,
                transformedExtent.Value.MaxY,
                outputSrid.Value)
            : null;
    }

    internal static bool CanFallbackToLayerExtent(QueryParameters queryParams)
    {
        ArgumentNullException.ThrowIfNull(queryParams);

        if (queryParams.ObjectIds is { Length: > 0 } ||
            !string.IsNullOrWhiteSpace(queryParams.Geometry) ||
            !string.IsNullOrWhiteSpace(queryParams.Time))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(queryParams.Where))
        {
            return true;
        }

        var normalizedWhere = string.Concat(queryParams.Where.Where(ch => !char.IsWhiteSpace(ch)));
        return string.Equals(normalizedWhere, "1=1", StringComparison.Ordinal);
    }

    private static ValidationResult ValidateBboxAreaLimit(
        GeoServicesGeometry geometry,
        SpatialReference spatialReference,
        double maxBboxAreaSqKm)
    {
        if (maxBboxAreaSqKm <= 0)
        {
            return ValidationResult.Success();
        }

        if (!TryGetGeometryEnvelope(geometry, out var minX, out var minY, out var maxX, out var maxY))
        {
            return ValidationResult.Success();
        }

        // Dateline-crossing envelopes intentionally bypass the bbox-area limit.
        // Their wrapped width is protocol-valid, but a naive area clamp would
        // reject legitimate Pacific queries and browser `.within(bounds)` calls.
        if (spatialReference.IsGeographic && minX > maxX)
        {
            return ValidationResult.Success();
        }

        if (!TryCalculateBoundingBoxAreaSqKm(minX, minY, maxX, maxY, spatialReference, out var bboxAreaSqKm, out var areaError))
        {
            return ValidationResult.Failure(areaError!);
        }

        if (bboxAreaSqKm <= maxBboxAreaSqKm)
        {
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(
            $"Geometry bounding box area ({bboxAreaSqKm.ToString("F2", CultureInfo.InvariantCulture)} sq km) exceeds maximum allowed area ({maxBboxAreaSqKm.ToString("F2", CultureInfo.InvariantCulture)} sq km).");
    }

    private static ValidationResult ValidateGeometryCoordinates(
        GeoServicesGeometry geometry,
        SpatialReference spatialReference)
    {
        var isGeographic = spatialReference.IsGeographic;

        if (!TryValidateCoordinatePair(geometry.X, geometry.Y, isGeographic, out var errorMessage))
        {
            return ValidationResult.Failure(errorMessage!);
        }

        var hasEnvelopeCoordinates = geometry.Xmin.HasValue || geometry.Ymin.HasValue || geometry.Xmax.HasValue || geometry.Ymax.HasValue;
        if (hasEnvelopeCoordinates)
        {
            if (!geometry.Xmin.HasValue || !geometry.Ymin.HasValue || !geometry.Xmax.HasValue || !geometry.Ymax.HasValue)
            {
                return ValidationResult.Failure("Envelope geometry must include xmin, ymin, xmax, and ymax values.");
            }

            if (!TryValidateCoordinatePair(geometry.Xmin, geometry.Ymin, isGeographic, out errorMessage) ||
                !TryValidateCoordinatePair(geometry.Xmax, geometry.Ymax, isGeographic, out errorMessage))
            {
                return ValidationResult.Failure(errorMessage!);
            }

            if (geometry.Ymin.Value > geometry.Ymax.Value)
            {
                return ValidationResult.Failure("Envelope latitude range is invalid.");
            }

            if (!isGeographic && geometry.Xmin.Value > geometry.Xmax.Value)
            {
                return ValidationResult.Failure("Envelope x range is invalid.");
            }
        }

        if (!TryValidateCoordinateCollection(geometry.Points, isGeographic, out errorMessage) ||
            !TryValidateCoordinatePathCollection(geometry.Paths, isGeographic, out errorMessage) ||
            !TryValidateCoordinatePathCollection(geometry.Rings, isGeographic, out errorMessage))
        {
            return ValidationResult.Failure(errorMessage!);
        }

        return ValidationResult.Success();
    }

    private static bool TryGetGeometryEnvelope(
        GeoServicesGeometry geometry,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        minX = 0;
        minY = 0;
        maxX = 0;
        maxY = 0;
        var hasCoordinates = false;

        if (geometry.Xmin.HasValue && geometry.Ymin.HasValue && geometry.Xmax.HasValue && geometry.Ymax.HasValue)
        {
            minX = geometry.Xmin.Value;
            minY = geometry.Ymin.Value;
            maxX = geometry.Xmax.Value;
            maxY = geometry.Ymax.Value;
            hasCoordinates = true;
        }

        hasCoordinates = ExtendEnvelope(geometry.X, geometry.Y, ref minX, ref minY, ref maxX, ref maxY, hasCoordinates) || hasCoordinates;

        if (geometry.Points != null)
        {
            foreach (var point in geometry.Points)
            {
                if (point is not { Length: >= 2 })
                {
                    continue;
                }

                hasCoordinates = ExtendEnvelope(point[0], point[1], ref minX, ref minY, ref maxX, ref maxY, hasCoordinates) || hasCoordinates;
            }
        }

        if (geometry.Paths != null)
        {
            foreach (var path in geometry.Paths)
            {
                if (path == null)
                {
                    continue;
                }

                foreach (var coordinate in path)
                {
                    if (coordinate is not { Length: >= 2 })
                    {
                        continue;
                    }

                    hasCoordinates = ExtendEnvelope(coordinate[0], coordinate[1], ref minX, ref minY, ref maxX, ref maxY, hasCoordinates) || hasCoordinates;
                }
            }
        }

        if (geometry.Rings != null)
        {
            foreach (var ring in geometry.Rings)
            {
                if (ring == null)
                {
                    continue;
                }

                foreach (var coordinate in ring)
                {
                    if (coordinate is not { Length: >= 2 })
                    {
                        continue;
                    }

                    hasCoordinates = ExtendEnvelope(coordinate[0], coordinate[1], ref minX, ref minY, ref maxX, ref maxY, hasCoordinates) || hasCoordinates;
                }
            }
        }

        return hasCoordinates;
    }

    private static bool ExtendEnvelope(
        double? x,
        double? y,
        ref double minX,
        ref double minY,
        ref double maxX,
        ref double maxY,
        bool hasCoordinates)
    {
        if (!x.HasValue || !y.HasValue || !double.IsFinite(x.Value) || !double.IsFinite(y.Value))
        {
            return false;
        }

        if (!hasCoordinates)
        {
            minX = x.Value;
            maxX = x.Value;
            minY = y.Value;
            maxY = y.Value;
            return true;
        }

        minX = Math.Min(minX, x.Value);
        minY = Math.Min(minY, y.Value);
        maxX = Math.Max(maxX, x.Value);
        maxY = Math.Max(maxY, y.Value);
        return true;
    }

    private static bool TryValidateCoordinateCollection(
        double[][]? coordinates,
        bool isGeographic,
        out string? errorMessage)
    {
        errorMessage = null;

        if (coordinates == null)
        {
            return true;
        }

        foreach (var coordinate in coordinates)
        {
            if (coordinate is not { Length: >= 2 })
            {
                errorMessage = "Geometry coordinate pairs must include both x and y values.";
                return false;
            }

            if (!TryValidateCoordinatePair(coordinate[0], coordinate[1], isGeographic, out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateCoordinatePathCollection(
        double[][][]? paths,
        bool isGeographic,
        out string? errorMessage)
    {
        errorMessage = null;

        if (paths == null)
        {
            return true;
        }

        foreach (var path in paths)
        {
            if (!TryValidateCoordinateCollection(path, isGeographic, out errorMessage))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateCoordinatePair(
        double? x,
        double? y,
        bool isGeographic,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!x.HasValue && !y.HasValue)
        {
            return true;
        }

        if (!x.HasValue || !y.HasValue)
        {
            errorMessage = "Geometry coordinate pairs must include both x and y values.";
            return false;
        }

        if (!double.IsFinite(x.Value) || !double.IsFinite(y.Value))
        {
            errorMessage = "Geometry contains non-finite coordinate values.";
            return false;
        }

        if (!isGeographic)
        {
            return true;
        }

        if (x.Value < -180.0 || x.Value > 180.0)
        {
            errorMessage = "Geographic longitude values must be between -180 and 180 degrees.";
            return false;
        }

        if (y.Value < -90.0 || y.Value > 90.0)
        {
            errorMessage = "Geographic latitude values must be between -90 and 90 degrees.";
            return false;
        }

        return true;
    }

    private static string CreateSpatialReferenceErrorMessage(string parameterName, string? rawValue)
        => string.IsNullOrWhiteSpace(rawValue)
            ? $"{parameterName} must not be empty."
            : $"Unsupported {parameterName} value: {rawValue}";

    private static SpatialReference ResolveAreaLimitSpatialReference(
        LayerDefinition layer,
        GeoServicesGeometry geometry,
        int srid)
    {
        var geometrySpatialReference = geometry.SpatialReference;
        if (!string.IsNullOrWhiteSpace(geometrySpatialReference?.Wkt))
        {
            return SpatialReference.Create(
                srid,
                geometrySpatialReference.LatestWkid,
                geometrySpatialReference.VcsWkid,
                geometrySpatialReference.LatestVcsWkid,
                geometrySpatialReference.Wkt);
        }

        if (layer.SpatialReference.ToSrid() == srid)
        {
            return layer.SpatialReference;
        }

        return srid switch
        {
            4326 => SpatialReference.WGS84,
            3857 => SpatialReference.WebMercator,
            _ => SpatialReference.Create(srid)
        };
    }

    private static bool TryCalculateBoundingBoxAreaSqKm(
        double minX,
        double minY,
        double maxX,
        double maxY,
        SpatialReference spatialReference,
        out double areaSqKm,
        out string? errorMessage)
    {
        areaSqKm = 0;
        errorMessage = null;

        if (spatialReference.IsGeographic)
        {
            areaSqKm = CalculateGeographicAreaSqKm(minX, minY, maxX, maxY);
            return true;
        }

        if (!TryResolveProjectedMetersPerUnit(spatialReference, out var metersPerUnit))
        {
            errorMessage =
                $"Geometry bounding box area limit cannot be evaluated for projected SRID {spatialReference.Wkid} because its linear units are unknown. Specify geometry in a geographic CRS or provide WKT for the projected CRS.";
            return false;
        }

        var width = Math.Abs(maxX - minX);
        var height = Math.Abs(maxY - minY);
        areaSqKm = width * height * metersPerUnit * metersPerUnit / 1_000_000.0;
        return true;
    }

    private static double CalculateGeographicAreaSqKm(double minLon, double minLat, double maxLon, double maxLat)
    {
        const double earthRadiusKm = 6371.0088;

        var normalizedMinLat = Math.Clamp(minLat, -90.0, 90.0);
        var normalizedMaxLat = Math.Clamp(maxLat, -90.0, 90.0);
        var minLatRad = DegreesToRadians(Math.Min(normalizedMinLat, normalizedMaxLat));
        var maxLatRad = DegreesToRadians(Math.Max(normalizedMinLat, normalizedMaxLat));

        var longitudeSpan = maxLon >= minLon
            ? maxLon - minLon
            : (360.0 - minLon) + maxLon;
        longitudeSpan = Math.Clamp(Math.Abs(longitudeSpan), 0.0, 360.0);
        var longitudeSpanRad = DegreesToRadians(longitudeSpan);

        var sphericalBand = Math.Abs(Math.Sin(maxLatRad) - Math.Sin(minLatRad));
        return earthRadiusKm * earthRadiusKm * sphericalBand * longitudeSpanRad;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static bool TryResolveProjectedMetersPerUnit(SpatialReference spatialReference, out double metersPerUnit)
    {
        metersPerUnit = 0;

        if (spatialReference.IsGeographic)
        {
            return false;
        }

        if (spatialReference.Wkid == SpatialReference.WebMercator.Wkid)
        {
            metersPerUnit = 1.0;
            return true;
        }

        if (string.IsNullOrWhiteSpace(spatialReference.Wkt))
        {
            return false;
        }

        var matches = _projectedUnitFactorPattern.Matches(spatialReference.Wkt);
        if (matches.Count == 0)
        {
            return false;
        }

        var factorValue = matches[^1].Groups["factor"].Value;
        if (!double.TryParse(factorValue, NumberStyles.Float, CultureInfo.InvariantCulture, out metersPerUnit))
        {
            metersPerUnit = 0;
            return false;
        }

        return double.IsFinite(metersPerUnit) && metersPerUnit > 0;
    }

    // Parameters that change *result semantics* — rejecting them is correct because
    // silently ignoring would return output that differs from what the client asked for.
    //
    // Compatibility parameters that ArcGIS clients routinely send by default
    // (gdbVersion, quantizationParameters, datumTransformation, unknown resultType)
    // are silently accepted to preserve interop; a fail-closed response here would
    // break out-of-the-box connections from ArcGIS Pro and the JS API.
    private static bool TryValidateUnsupportedParameters(QueryParameters queryParams, out string? errorMessage)
    {
        var unsupported = new List<string>();

        if (queryParams.ReturnTrueCurves)
        {
            unsupported.Add("returnTrueCurves");
        }

        if (queryParams.ReturnExceededLimitFeatures)
        {
            unsupported.Add("returnExceededLimitFeatures");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Having))
        {
            unsupported.Add("having");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.SqlFormat))
        {
            unsupported.Add("sqlFormat");
        }

        if (queryParams.ReturnCentroid)
        {
            unsupported.Add("returnCentroid");
        }

        if (unsupported.Count == 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"Unsupported query parameters: {string.Join(", ", unsupported)}.";
        return false;
    }

    private static bool TryParseStatisticsDefinitions(
        string outStatisticsJson,
        LayerDefinition layer,
        out ImmutableArray<StatisticDefinition> definitions,
        out string? error)
    {
        error = null;
        definitions = default;

        try
        {
            using var document = JsonDocument.Parse(outStatisticsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "outStatistics must be a JSON array.";
                return false;
            }

            var defs = new List<StatisticDefinition>();
            var fieldNames = new HashSet<string>(
                layer.Fields.Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);
            // Also allow objectid
            fieldNames.Add(FieldNames.ObjectId);

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("statisticType", out var typeElement) ||
                    !element.TryGetProperty("onStatisticField", out var fieldElement) ||
                    !element.TryGetProperty("outStatisticFieldName", out var aliasElement))
                {
                    error = "Each statistic definition must have statisticType, onStatisticField, and outStatisticFieldName.";
                    return false;
                }

                var statisticTypeStr = typeElement.GetString();
                var onField = fieldElement.GetString();
                var outAlias = aliasElement.GetString();

                if (string.IsNullOrWhiteSpace(statisticTypeStr) ||
                    string.IsNullOrWhiteSpace(onField) ||
                    string.IsNullOrWhiteSpace(outAlias))
                {
                    error = "statisticType, onStatisticField, and outStatisticFieldName must not be empty.";
                    return false;
                }

                if (!fieldNames.Contains(onField))
                {
                    error = $"Field '{onField}' does not exist on the layer.";
                    return false;
                }

                if (!TryParseStatisticType(statisticTypeStr, out var statisticType))
                {
                    error = $"Unsupported statisticType: '{statisticTypeStr}'. Supported types: count, sum, min, max, avg, stddev, var.";
                    return false;
                }

                defs.Add(new StatisticDefinition
                {
                    StatisticType = statisticType,
                    OnStatisticField = onField,
                    OutStatisticFieldName = outAlias
                });
            }

            if (defs.Count == 0)
            {
                error = "outStatistics array must not be empty.";
                return false;
            }

            definitions = defs.ToImmutableArray();
            return true;
        }
        catch (JsonException)
        {
            error = InvalidOutStatisticsJsonMessage;
            return false;
        }
    }

    private static bool TryParseStatisticType(string value, out StatisticType statisticType)
    {
        statisticType = default;
        if (string.Equals(value, "count", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Count;
            return true;
        }

        if (string.Equals(value, "sum", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Sum;
            return true;
        }

        if (string.Equals(value, "min", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Min;
            return true;
        }

        if (string.Equals(value, "max", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Max;
            return true;
        }

        if (string.Equals(value, "avg", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Avg;
            return true;
        }

        if (string.Equals(value, "stddev", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Stddev;
            return true;
        }

        if (string.Equals(value, "var", StringComparison.OrdinalIgnoreCase))
        {
            statisticType = StatisticType.Var;
            return true;
        }

        return false;
    }

    private static bool IsClientSafeInvalidOperation(InvalidOperationException exception)
    {
        if (string.IsNullOrWhiteSpace(exception.Message))
        {
            return false;
        }

        return exception.Message.StartsWith("Invalid query", StringComparison.OrdinalIgnoreCase)
            || exception.Message.StartsWith("Invalid spatial parameters", StringComparison.OrdinalIgnoreCase)
            || exception.Message.StartsWith("Invalid geometry", StringComparison.OrdinalIgnoreCase)
            || exception.Message.StartsWith("Invalid orderByFields", StringComparison.OrdinalIgnoreCase)
            || exception.Message.StartsWith("Unknown orderByFields", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("Geometry is required for nearest neighbor queries", StringComparison.OrdinalIgnoreCase);
    }

    private static QueryResult<Feature> ApplyDistinctValues(QueryResult<Feature> result, string[] outFields)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = ImmutableArray.CreateBuilder<Feature>();

        foreach (var feature in result.Items)
        {
            var key = BuildDistinctKey(feature, outFields);
            if (seen.Add(key))
            {
                distinct.Add(feature);
            }
        }

        return QueryResult<Feature>.Create(distinct.Count, distinct.ToImmutable());
    }

    private static string ResolveTelemetryProtocol(string? requiredProtocol)
        => string.Equals(requiredProtocol, ServiceProtocols.MapServer, StringComparison.OrdinalIgnoreCase)
            ? HonuaTelemetry.Protocols.MapServer
            : HonuaTelemetry.Protocols.FeatureServer;

    private static QueryResult<Feature> ApplyPaginationWindow(
        QueryResult<Feature> result,
        int? offset,
        int? limit)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return result;
        }

        var totalCount = result.Items.Length;
        var effectiveOffset = Math.Max(0, offset ?? 0);
        if (effectiveOffset >= totalCount)
        {
            return QueryResult<Feature>.Create(totalCount, ImmutableArray<Feature>.Empty, false);
        }

        var remaining = totalCount - effectiveOffset;
        var effectiveLimit = limit.HasValue
            ? Math.Max(0, limit.Value)
            : remaining;
        var take = Math.Min(remaining, effectiveLimit);
        var pageItems = result.Items
            .Skip(effectiveOffset)
            .Take(take)
            .ToImmutableArray();
        var hasMore = effectiveOffset + take < totalCount;

        return QueryResult<Feature>.Create(totalCount, pageItems, hasMore);
    }

    private static string BuildDistinctKey(Feature feature, string[] outFields)
    {
        var parts = new string[outFields.Length];
        for (var i = 0; i < outFields.Length; i++)
        {
            var fieldName = outFields[i];
            if (feature.Attributes.TryGetValue(fieldName, out var value) && value != null)
            {
                parts[i] = value switch
                {
                    IConvertible convertible => convertible.ToString(CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty
                };
            }
            else
            {
                parts[i] = "\0null\0";
            }
        }

        return string.Join("\0", parts);
    }
}

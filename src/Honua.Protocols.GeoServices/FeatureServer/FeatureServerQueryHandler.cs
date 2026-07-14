// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Parsing;
using Honua.Infrastructure.Services;
using Honua.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Handler for FeatureServer query operations.
/// </summary>
internal sealed partial class FeatureServerQueryHandler(
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
    private readonly IDatumTransformationCatalog _datumTransformationCatalog = dependencies.DatumTransformationCatalog;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly ILogger<FeatureServerQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int StreamingThreshold = 1000;
    private const string FeatureServerProtocol = "FeatureServer";
    private const string MapServerProtocol = "MapServer";
    private const string InvalidTimeParameterMessage = "Invalid time parameter.";
    private const string InvalidOutStatisticsJsonMessage = "outStatistics must be a valid JSON array.";

    private sealed record QueryLayerContext(
        MetadataV2Service Service,
        MetadataV2Publication Publication,
        MetadataV2Resource Resource,
        int PublicLayerId,
        int StorageLayerId);

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

    private async Task<(QueryLayerContext? Layer, IResult? Error)> ResolveQueryLayerContextV2Async(
        string serviceId,
        int layerId,
        HttpContext context,
        string? requiredProtocol,
        CancellationToken cancellationToken)
    {
        var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerV2Async(
            _resourceValidator,
            serviceId,
            layerId,
            context,
            _logger,
            requiredProtocol ?? FeatureServerProtocol,
            cancellationToken).ConfigureAwait(false);
        if (!resourceValidationResult.IsValid)
        {
            return (null, resourceValidationResult.ErrorResult!);
        }

        var service = resourceValidationResult.Service!;
        var publication = resourceValidationResult.Publication!;
        var resource = resourceValidationResult.Resource!;

        // Per-operation RBAC grants are consulted first (#1375); the coarse
        // AccessPolicy seam is the fallback when no grant matches. This is the
        // enforced read path wired to the resolver for this PR â€” the remaining
        // protocol adapters are re-wired in #1376.
        var accessError = await AccessPolicyHelpers.RequireResourceAccessAsync(
            context,
            resource,
            service,
            AccessScope.Read,
            cancellationToken).ConfigureAwait(false);
        if (accessError != null)
        {
            return (null, accessError);
        }

        var snapshotProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await snapshotProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource)
            ?? publication.LayerIndex;
        if (!storageLayerId.HasValue)
        {
            return (null, StandardErrorHelpers.CreateNotFound(
                context,
                $"Layer {layerId} is not bound to feature storage."));
        }

        return (new QueryLayerContext(
            service,
            publication,
            resource,
            publication.LayerIndex ?? layerId,
            storageLayerId.Value), null);
    }

    public async Task<(QueryResponse? Response, IResult? Error)> HandleServiceQueryLayerAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        HttpContext context,
        ICommonQueryValidator queryValidator,
        string? requiredProtocol = null,
        CancellationToken cancellationToken = default)
    {
        // Not converted to a `using` declaration: featureActivity starts as null and is
        // reassigned once telemetry context is available further down, but using-declared
        // variables are read-only (CS1656) — the manual finally-dispose is required here.
        Activity? featureActivity = null;
        try
        {
            var hasWhereClause = !string.IsNullOrWhiteSpace(queryParams.Where);
            FeatureServerLog.QueryRequested(
                _logger,
                serviceId,
                layerId,
                hasWhereClause);

            var (layerContext, validationError) = await ResolveQueryLayerContextV2Async(
                serviceId,
                layerId,
                context,
                requiredProtocol,
                cancellationToken).ConfigureAwait(false);
            if (validationError != null)
            {
                return (null, validationError);
            }

            var queryLayer = layerContext!;
            var telemetryProtocol = ResolveTelemetryProtocol(requiredProtocol);
            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, telemetryProtocol);
            requestActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

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

            if (!TryValidateSqlFormat(validatedParams, out var sqlFormatError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid sqlFormat",
                    [sqlFormatError!]));
            }

            // A having clause only filters aggregated groups, so reject it when no
            // outStatistics are present rather than silently ignoring it.
            if (!string.IsNullOrWhiteSpace(validatedParams.Having) &&
                string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid having",
                    ["having requires outStatistics (and typically groupByFieldsForStatistics)."]));
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
                queryLayer.Resource,
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

            // BH2-001: Reject returnDistinctValues when the resultOffset exceeds the safe in-memory
            // scan threshold (10 Ã— MaxRecordCount). Serving a high-offset distinct page requires
            // loading every row up to the offset before deduplication, making the effective scan
            // size proportional to the offset regardless of the requested page size.
            if (validatedParams.ReturnDistinctValues && (query.Value.Offset ?? 0) > queryLimits.MaxRecordCount * 10)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context,
                    "Unsupported query parameters",
                    [$"returnDistinctValues is not supported with resultOffset exceeding {queryLimits.MaxRecordCount * 10}."]));
            }

            var response = await ExecuteJsonQueryResponseAsync(
                serviceId,
                layerId,
                queryLayer,
                validatedParams,
                query.Value,
                outputSrid,
                queryLimits,
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
        catch (NotSupportedException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return (null, StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter));
        }
        catch (DbException ex) when (IsClientDataException(ex))
        {
            // Eval-time SQL data exceptions (e.g. division-by-zero or numeric
            // overflow in a `where` arithmetic expression, a bad CAST) are caused
            // by malformed client input, not a server fault. They surface only at
            // execution time, after up-front validation, so map their data-exception
            // SQL states to a structured 400 instead of letting them fall through to
            // the generic 500 path. Provider-agnostic via DbException.SqlState.
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);
            return (null, StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter));
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure must map to a generic 500 rather than crash the request.
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
        // Not converted to a `using` declaration: featureActivity starts as null and is
        // reassigned once telemetry context is available further down, but using-declared
        // variables are read-only (CS1656) — the manual finally-dispose is required here.
        Activity? featureActivity = null;
        try
        {
            var hasWhereClause = !string.IsNullOrWhiteSpace(queryParams.Where);
            FeatureServerLog.QueryRequested(
                _logger,
                serviceId,
                layerId,
                hasWhereClause);

            var (layerContext, validationError) = await ResolveQueryLayerContextV2Async(
                serviceId,
                layerId,
                context,
                requiredProtocol,
                cancellationToken).ConfigureAwait(false);
            if (validationError != null)
            {
                return validationError;
            }

            var queryLayer = layerContext!;
            var telemetryProtocol = ResolveTelemetryProtocol(requiredProtocol);
            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, telemetryProtocol);
            requestActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

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

            if (!TryValidateSqlFormat(validatedParams, out var sqlFormatError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid sqlFormat",
                    [sqlFormatError!]);
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

            // f=pjson is byte-identical to f=json except for indentation. TryValidateOutputFormat
            // collapses pjson to json, so recover the pretty flag from the raw requested format
            // and indent the serialized JSON payload below (#1824).
            var prettyJson = string.Equals(requestedFormat?.Trim(), "pjson", StringComparison.OrdinalIgnoreCase);

            // returnCountOnly/returnIdsOnly/returnExtentOnly produce scalar/structural results,
            // not a GeoJSON FeatureCollection. The format validator allow-lists geojson for the
            // query operation, but these secondary modes cannot honor it â€” return a clean 400
            // rather than silently downgrading to an Esri-JSON 200 (#1824).
            if (string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase) &&
                (validatedParams.ReturnCountOnly || validatedParams.ReturnIdsOnly || validatedParams.ReturnExtentOnly))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    ["f=geojson is not supported for returnCountOnly, returnIdsOnly, or returnExtentOnly queries."]);
            }

            var canCache = !ShouldBypassQueryResponseCache(validatedParams) &&
                           ResponseCacheUtilities.ShouldCache(context, _cacheOptions);
            var cacheTtl = canCache ? _cacheOptions.GetQueryTtlWithJitter() : TimeSpan.Zero;
            if (canCache && cacheTtl <= TimeSpan.Zero)
            {
                canCache = false;
            }

            string? cacheKey = null;

            string? GetCacheKey()
            {
                if (!canCache)
                {
                    return null;
                }

                return cacheKey ??= ResponseCacheUtilities.BuildFeatureServerKey(serviceId, layerId, context.Request);
            }

            async Task<IResult?> TryGetCachedResponseAsync()
            {
                var currentCacheKey = GetCacheKey();
                if (currentCacheKey is null)
                {
                    return null;
                }

                var cached = await _responseCache.GetAsync<CachedResponse>(currentCacheKey, cancellationToken);
                return cached == null
                    ? null
                    : ResponseCacheUtilities.CreateResultFromCachedResponse(context, cached, _etagService);
            }

            async Task<IResult> CreateCachedResultAsync<T>(
                T response,
                JsonTypeInfo<T> typeInfo,
                string contentType)
            {
                var currentCacheKey = GetCacheKey();
                if (currentCacheKey is null)
                {
                    // f=pjson is JSON with indentation; re-indent the serialized payload rather
                    // than returning the compact Results.Json so pjson is not byte-identical to
                    // json (#1824).
                    if (prettyJson)
                    {
                        var prettyPayload = JsonReindenter.ToIndentedUtf8Bytes(
                            JsonSerializer.SerializeToUtf8Bytes(response, typeInfo));
                        return Results.Bytes(prettyPayload, contentType);
                    }

                    return Results.Json(response, typeInfo, contentType: contentType);
                }

                var payload = JsonSerializer.SerializeToUtf8Bytes(response, typeInfo);
                if (prettyJson)
                {
                    payload = JsonReindenter.ToIndentedUtf8Bytes(payload);
                }

                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(currentCacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            async Task<IResult> CreateCachedBytesResultAsync(byte[] payload, string contentType)
            {
                var currentCacheKey = GetCacheKey();
                if (currentCacheKey is null)
                {
                    return Results.Bytes(payload, contentType);
                }

                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payload, contentType, _etagService);
                await _responseCache.SetAsync(currentCacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            async Task<IResult> CreateCachedMemoryResultAsync(ReadOnlyMemory<byte> payload, string contentType)
            {
                var currentCacheKey = GetCacheKey();
                if (currentCacheKey is null)
                {
                    return Results.Bytes(payload, contentType);
                }

                var payloadArray = payload.ToArray();
                var cachedResponse = ResponseCacheUtilities.CreateCachedResponse(payloadArray, contentType, _etagService);
                await _responseCache.SetAsync(currentCacheKey, cachedResponse, cacheTtl, cancellationToken);
                return ResponseCacheUtilities.CreateResultFromCachedResponse(context, cachedResponse, _etagService);
            }

            var (preparedQuery, outputSrid, preparationError) = await PrepareFeatureQueryAsync(
                context,
                queryLayer.Resource,
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

            // Recover a client-requested Web Mercator alias outSR (e.g. 102100 -> 3857) so the JSON
            // response spatialReference echoes {wkid:102100, latestWkid:3857} (#2736). When an alias
            // echo applies the raw point fast path is skipped so the response flows through the
            // materialized formatter that carries the echo.
            var requestedOutSrAlias = await ResolveRequestedWebMercatorAliasAsync(validatedParams, outputSrid, cancellationToken).ConfigureAwait(false);

            if (canCache && ResponseCacheUtilities.ShouldBypassAdHocSpatialResponseCache(query, validatedParams.Where))
            {
                canCache = false;
            }

            // A having clause filters aggregated groups, so it is only meaningful
            // alongside outStatistics. Reject it explicitly rather than silently
            // ignoring it on a non-statistics query.
            if (!string.IsNullOrWhiteSpace(validatedParams.Having) &&
                string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid having",
                    ["having requires outStatistics (and typically groupByFieldsForStatistics)."]);
            }

            // Handle statistics queries (outStatistics)
            if (!string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
            {
                if (!TryParseStatisticsDefinitions(validatedParams.OutStatistics, queryLayer.Resource, out var statisticsDefs, out var statsError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid outStatistics",
                        [statsError ?? InvalidOutStatisticsJsonMessage]);
                }

                ImmutableArray<string>? groupByFields = null;
                if (!string.IsNullOrWhiteSpace(validatedParams.GroupByFieldsForStatistics))
                {
                    if (!TryParseGroupByFields(validatedParams.GroupByFieldsForStatistics, queryLayer.Resource, out var parsedGroupBy, out var groupByError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context,
                            "Invalid groupByFieldsForStatistics",
                            [groupByError ?? "groupByFieldsForStatistics must contain valid field names."]);
                    }

                    groupByFields = parsedGroupBy;
                }

                ImmutableArray<HavingCondition>? havingConditions = null;
                if (!string.IsNullOrWhiteSpace(validatedParams.Having))
                {
                    if (!TryParseHavingConditions(validatedParams.Having, statisticsDefs, queryLayer.Resource, out var parsedHaving, out var havingError))
                    {
                        return StandardErrorHelpers.CreateBadRequest(context,
                            "Invalid having",
                            [havingError ?? "having clause could not be parsed."]);
                    }

                    havingConditions = parsedHaving;
                }

                // BH7-003: Apply the configured MaxRecordCount as an upper bound on
                // statistics group rows. Without this cap a groupByFieldsForStatistics
                // on a high-cardinality column (e.g. objectId) forces the server to
                // materialise every row from the database — an OOM/DoS vector.
                var statisticsQuery = query with
                {
                    OutStatistics = statisticsDefs,
                    GroupByFields = groupByFields,
                    Having = havingConditions,
                    Limit = queryLimits.MaxRecordCount,
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
                var statisticsRows = await _queryExecutor.QueryStatisticsAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    statisticsQuery,
                    cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "statistics", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

                var statisticsFeatures = statisticsRows.Select(row => new GeoServicesFeature
                {
                    Attributes = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase),
                    Geometry = null,
                    // Aggregate rows have no geometry; omit the geometry key entirely (Esri parity).
                    IncludeGeometry = false
                }).ToArray();

                HonuaTelemetry.SetSuccess(featureActivity, statisticsFeatures.Length);
                var statisticsResponse = new QueryResponse { Features = statisticsFeatures };
                return await CreateCachedResultAsync(statisticsResponse, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(queryLayer.Resource);

            // Esri parity: returnExtentOnly combined with returnCountOnly must return
            // {extent, count}, so the extent-only branch (which already emits both)
            // takes precedence over the count-only branch.
            if (validatedParams.ReturnCountOnly && !validatedParams.ReturnExtentOnly)
            {
                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var stopwatch = Stopwatch.StartNew();
                var count = await _queryExecutor.CountAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    query,
                    cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "count", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                var safeCount = (int)Math.Min(count, int.MaxValue);
                HonuaTelemetry.SetSuccess(featureActivity, safeCount);

                // f=pbf must return a protobuf-encoded count (CountResult arm of the
                // FeatureCollectionPBuffer QueryResult oneof), mirroring the feature path;
                // otherwise the count branch silently degrades to JSON. (#1775)
                if (string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase))
                {
                    var (countPayload, countContentType) = PbfQueryFormatter.FormatCountAsPbf(count);
                    return await CreateCachedBytesResultAsync(countPayload, countContentType);
                }

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
                var extent = await _queryExecutor.GetExtentAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    query,
                    cancellationToken).ConfigureAwait(false);
                // Esri's returnExtentOnly response is {extent, count}; report the number of
                // features that contributed to the computed envelope alongside the extent.
                var extentCount = await _queryExecutor.CountAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    query,
                    cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "extent", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                extent ??= await ResolveExtentFallbackAsync(context, validatedParams, queryLayer.Resource, outputSrid, cancellationToken);
                HonuaTelemetry.SetSuccess(featureActivity, (int)Math.Min(extentCount, int.MaxValue));
                var extentInfo = extent.HasValue ? extent.Value.ToExtentInfo() : null;

                // f=pbf must return the ExtentCountResult arm of the FeatureCollectionPBuffer
                // QueryResult oneof (application/x-protobuf), mirroring the count path; otherwise
                // the extent branch silently degrades to JSON (#1824).
                if (string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase) && extentInfo is not null)
                {
                    var (extentPayload, extentContentType) = PbfQueryFormatter.FormatExtentAsPbf(
                        extentInfo.Xmin,
                        extentInfo.Ymin,
                        extentInfo.Xmax,
                        extentInfo.Ymax,
                        extentInfo.SpatialReference.Wkid,
                        extentCount);
                    return await CreateCachedBytesResultAsync(extentPayload, extentContentType);
                }

                var response = new QueryResponse
                {
                    Extent = extentInfo,
                    Count = extentCount,
                    Features = null
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            if (validatedParams.ReturnIdsOnly)
            {
                // f=pbf must return the ObjectIdsResult arm of the FeatureCollectionPBuffer
                // QueryResult oneof (application/x-protobuf), mirroring the count path; the
                // JSON streaming writer cannot emit protobuf, so force materialization for pbf
                // and never take the streaming branch (#1824).
                var idsIsPbf = string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase);
                var idsEffectiveLimit = query.Limit ?? validatedParams.ObjectIds?.Length ?? queryLimits.DefaultRecordCount;
                var idsUseStreaming = !idsIsPbf && (!query.Limit.HasValue || idsEffectiveLimit > StreamingThreshold);
                if (idsUseStreaming)
                {
                    await _queryExecutor.StreamIdsAsync(
                        queryLayer.Service,
                        queryLayer.Resource,
                        queryLayer.Publication,
                        queryLayer.StorageLayerId,
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
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    query,
                    cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "ids", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

                var objectIds = result.Items
                    .Select(feature => GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName))
                    .ToArray();
                HonuaTelemetry.SetSuccess(featureActivity, objectIds.Length);

                if (idsIsPbf)
                {
                    var (idsPayload, idsContentType) = PbfQueryFormatter.FormatIdsAsPbf(objectIdFieldName, objectIds);
                    return await CreateCachedBytesResultAsync(idsPayload, idsContentType);
                }

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
            // Esri json coordinate quantization (quantizationParameters) applies only to the
            // GeoServices json featureSet, not geojson or the binary export formats.
            var isGeoJson = string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase);
            var isEsriJson = !isPbf && !isFgb && !isGeobuf && !isParquet && !isArrow && !isGeoJson;
            QuantizationTransform? quantizationTransform = null;
            if (isEsriJson &&
                !FeatureQuantizer.TryParse(validatedParams.QuantizationParameters, out quantizationTransform, out var quantizationError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, quantizationError ?? "Invalid quantizationParameters.");
            }

            // quantization must post-process the materialized featureSet, so route it through
            // the materialized path instead of the streaming writer. returnExceededLimitFeatures
            // is accept-and-ignore (#1460): Honua already returns the page plus
            // exceededTransferLimit, so it does not change the streaming decision.
            var useStreaming = effectiveLimit > StreamingThreshold && !isPbf && !isFgb && !isGeobuf && !isParquet && !isArrow
                && quantizationTransform is null;

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

                    if (!await _queryExecutor.SupportsFlatGeobufOutputAsync(
                            queryLayer.Service,
                            queryLayer.Resource,
                            queryLayer.Publication,
                            queryLayer.StorageLayerId,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Unsupported output format",
                            ["Output format 'fgb' is not supported by the configured feature store."]);
                    }

                    var fgbStopwatch = Stopwatch.StartNew();
                    var flatGeobufPayload = await _queryExecutor.QueryFlatGeobufWithValidationAsync(
                        queryLayer.Service,
                        queryLayer.Resource,
                        queryLayer.Publication,
                        queryLayer.StorageLayerId,
                        query,
                        cancellationToken).ConfigureAwait(false);
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

                    if (!await _queryExecutor.SupportsGeobufOutputAsync(
                            queryLayer.Service,
                            queryLayer.Resource,
                            queryLayer.Publication,
                            queryLayer.StorageLayerId,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            "Unsupported output format",
                            ["Output format 'geobuf' is not supported by the configured feature store."]);
                    }

                    var geobufStopwatch = Stopwatch.StartNew();
                    var geobufPayload = await _queryExecutor.QueryGeobufWithValidationAsync(
                        queryLayer.Service,
                        queryLayer.Resource,
                        queryLayer.Publication,
                        queryLayer.StorageLayerId,
                        query,
                        cancellationToken).ConfigureAwait(false);
                    geobufStopwatch.Stop();
                    FeatureServerLog.QueryExecuted(_logger, "query_geobuf", serviceId, layerId, geobufStopwatch.Elapsed.TotalMilliseconds);

                    var payload = geobufPayload ?? [];
                    FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, payload.Length > 0 ? 1 : 0, payload.Length > 0 ? 1 : 0);
                    HonuaTelemetry.SetSuccess(featureActivity, payload.Length > 0 ? 1 : 0);

                    return await CreateCachedBytesResultAsync(payload, "application/geobuf");
                }

                // PARQUET/ARROW are encoded entirely in managed code from the
                // materialized QueryResult<Feature> (GeoParquetFeatureWriter /
                // GeoArrow formatter), NOT by a store-native encoder â€” so unlike
                // FGB/GEOBUF they do not depend on the resolved store implementing
                // IFlatGeobufFeatureStore. Every IFeatureReader can materialize
                // features (QueryWithValidationAsync below), so gating parquet/arrow
                // on the FlatGeobuf marker incorrectly returned HTTP 400 for the
                // entire source-backed (provider-routed) FeatureServer surface,
                // whose storage-mapped reader omits that marker even though the
                // underlying PostGIS table fully supports GeoParquet export.
                // The earlier 500-leak this guard protected against was the generic
                // formatter throwing on a non-materializing store; parquet/arrow do
                // not hit that path, so no marker check is required here.

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

                // BH2-001: Reject returnDistinctValues when the resultOffset exceeds the safe in-memory
                // scan threshold (10 Ã— MaxRecordCount). Serving a high-offset distinct page requires
                // loading every row up to the offset before deduplication, making the effective scan
                // size proportional to the offset regardless of the requested page size.
                if (shouldApplyDistinct && (query.Offset ?? 0) > queryLimits.MaxRecordCount * 10)
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Unsupported query parameters",
                        [$"returnDistinctValues is not supported with resultOffset exceeding {queryLimits.MaxRecordCount * 10}."]);
                }

                // The raw point fast path emits a pre-serialized compact JSON payload; f=pjson
                // needs indentation, so skip the fast path and fall through to the materialized
                // formatter where the pretty flag is honored (#1824).
                if (quantizationTransform is null &&
                    !prettyJson &&
                    requestedOutSrAlias is null &&
                    CanUseRawGeoServicesPointFastPath(queryLayer.Resource, validatedParams, query, outputSrid, format) &&
                    await _queryExecutor.SupportsRawGeoServicesPointOutputAsync(
                        queryLayer.Service,
                        queryLayer.Resource,
                        queryLayer.Publication,
                        queryLayer.StorageLayerId,
                        cancellationToken).ConfigureAwait(false))
                {
                    var rawStopwatch = Stopwatch.StartNew();
                    var (payload, featureCount) = await _queryExecutor.QueryRawGeoServicesPointJsonWithValidationAsync(
                        queryLayer.Service,
                        queryLayer.Resource,
                        queryLayer.Publication,
                        queryLayer.StorageLayerId,
                        query,
                        validatedParams.ReturnGeometry,
                        outputSrid,
                        cancellationToken).ConfigureAwait(false);
                    rawStopwatch.Stop();
                    FeatureServerLog.QueryExecuted(_logger, "query_raw_geoservices", serviceId, layerId, rawStopwatch.Elapsed.TotalMilliseconds);
                    FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, featureCount, featureCount);
                    HonuaTelemetry.SetSuccess(featureActivity, featureCount);

                    return await CreateCachedMemoryResultAsync(payload, "application/json");
                }

                // DISTINCT is applied in memory, so bound the source-row scan at the
                // transfer limit (plus the requested window) instead of materializing
                // the whole layer; a truncated scan surfaces as exceededTransferLimit.
                var distinctScanLimit = ComputeDistinctScanLimit(query, queryLimits);
                var queryForExecution = shouldApplyDistinct
                    ? query with { Limit = distinctScanLimit, Offset = null }
                    : query;
                var queryStopwatch = Stopwatch.StartNew();
                // Pass the authenticated caller so plugin computed-field providers (#1562) can
                // scope derived values per-actor. Projection inside the executor is a no-op unless
                // the Enterprise plugin SDK is licensed, enabled, and a provider is registered.
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(
                    queryLayer.Service,
                    queryLayer.Resource,
                    queryLayer.Publication,
                    queryLayer.StorageLayerId,
                    queryForExecution,
                    context.User?.Identity?.Name,
                    cancellationToken).ConfigureAwait(false);
                queryStopwatch.Stop();
                var queryOperation = isParquet ? "query_parquet" : isArrow ? "query_arrow" : "query";
                FeatureServerLog.QueryExecuted(_logger, queryOperation, serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

                if (shouldApplyDistinct)
                {
                    var distinctScanTruncated = result.HasMoreResults || result.Items.Length >= distinctScanLimit;
                    result = ApplyDistinctValues(result, outFields!);
                    result = ApplyPaginationWindow(result, query.Offset, query.Limit, distinctScanTruncated);
                }

                (object? formattedResponse, string? contentType) = await _queryServices.FormatQueryResultAsync(
                    result,
                    queryLayer.Resource,
                    format,
                    validatedParams.ReturnGeometry,
                    outputSrid,
                    validatedParams.ReturnZ,
                    validatedParams.ReturnM,
                    validatedParams.GeometryPrecision,
                    validatedParams.MaxAllowableOffset,
                    outFields,
                    suppressObjectId: shouldApplyDistinct,
                    returnCentroid: validatedParams.ReturnCentroid,
                    requestedOutputSrid: requestedOutSrAlias);

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

                if (quantizationTransform is not null && formattedResponse is QueryResponse quantizableResponse)
                {
                    formattedResponse = ApplyQuantization(quantizableResponse, quantizationTransform);
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
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                query,
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
        catch (NotSupportedException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter);
        }
        catch (DbException ex) when (IsClientDataException(ex))
        {
            // Eval-time SQL data exceptions (division/mod-by-zero, numeric overflow,
            // invalid LOG/POWER/SQRT argument, bad CAST) raised by client `where`
            // arithmetic are malformed-input errors surfacing only at execution time.
            // Map their SQLSTATE class-22 codes to a structured 400 rather than 500.
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter);
        }
        catch (ParquetRuntimeUnavailableException ex)
        {
            // f=parquet requested on a runtime image that cannot load the native
            // ParquetSharp Arrow encoder (glibc-only; absent on Alpine/musl). Return a
            // clean 501 capability response instead of letting the native-load failure
            // surface as an unhandled 500 (honua-server#1942).
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateNotImplemented(context, ParquetRuntimeUnavailableException.CapabilityMessage);
        }
        // Intentionally generic: this is the top-level request handler boundary; any
        // unanticipated failure must map to a generic 500 (or the already-started
        // streaming result) rather than crash the request.
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

    private static bool CanUseRawGeoServicesPointFastPath(
        MetadataV2Resource resource,
        QueryParameters parameters,
        FeatureQuery query,
        int? outputSrid,
        string format)
    {
        if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (resource.ReadGeometryType() != MetadataV2GeometryType.Point)
        {
            return false;
        }

        if (!parameters.ReturnGeometry ||
            parameters.ReturnZ ||
            parameters.ReturnM ||
            parameters.ReturnCentroid ||
            parameters.ReturnDistance ||
            parameters.ReturnDistinctValues ||
            parameters.GeometryPrecision.HasValue ||
            parameters.MaxAllowableOffset.HasValue ||
            parameters.NearestCount.HasValue)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(parameters.OutFields) &&
            !parameters.OutFields.Trim().Equals("*", StringComparison.Ordinal))
        {
            return false;
        }

        if (query.OutFields.HasValue && !query.OutFields.Value.IsDefaultOrEmpty)
        {
            return false;
        }

        var layerSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        if (outputSrid.HasValue && outputSrid.Value != layerSrid)
        {
            return false;
        }

        return true;
    }

    internal static bool ShouldBypassQueryResponseCache(QueryParameters parameters)
        => !string.IsNullOrWhiteSpace(parameters.Geometry) ||
           !string.IsNullOrWhiteSpace(parameters.GeometryType) ||
           parameters.NearestCount.HasValue ||
           parameters.Distance.HasValue ||
           !string.IsNullOrWhiteSpace(parameters.Units) ||
           parameters.ReturnDistance ||
           !string.IsNullOrWhiteSpace(parameters.SpatialRel);

    private async Task<(FeatureQuery? Query, int? OutputSrid, IResult? Error)> PrepareFeatureQueryAsync(
        HttpContext context,
        MetadataV2Resource resource,
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
            inputSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        }

        if (parsedGeometry != null && inputSrid.HasValue)
        {
            var queryGeometrySpatialReference = ResolveAreaLimitSpatialReference(resource, parsedGeometry, inputSrid.Value);

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

        // Vertical (geoid/ellipsoidal) datum transformations are not yet supported. When
        // outSR carries a vertical CS (vcsWkid), fail explicitly rather than silently
        // ignoring the vertical component and returning horizontally-only reprojected Z.
        if (VerticalCoordinateSystemHelpers.RequestsVerticalTransform(validatedParams.OutSr))
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Unsupported vertical transformation",
                ["outSR specifies a vertical coordinate system (vcsWkid); vertical datum transformations are not supported."]));
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
            && resource.ReadGeometryType() != MetadataV2GeometryType.None
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
            if (filterExpression != null && IsNoOpFilterExpression(filterExpression))
            {
                filterExpression = null;
            }

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
                    resource);
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

        var useObjectIdsFastPath = ShouldUseInternalObjectIdsFastPath(resource);
        if (!useObjectIdsFastPath &&
            validatedParams.ObjectIds is { Length: > 0 } objectIds &&
            TryCreateObjectIdsExpression(resource, objectIds, out var objectIdsExpression))
        {
            filterExpression = filterExpression is null
                ? objectIdsExpression
                : new BinaryExpression(filterExpression, BinaryOperator.And, objectIdsExpression);
        }

        SqlFragment? sqlFilter = null;
        if (filterExpression != null)
        {
            var translationResult = _filterExpressionService.Translate(filterExpression, resource);
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
                SqlFilter = sqlFilter,
                UseObjectIdsFastPath = useObjectIdsFastPath
            },
            resource,
            cancellationToken).ConfigureAwait(false);
        if (!queryAdapterResult.IsSuccess || queryAdapterResult.Query == null)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [queryAdapterResult.ErrorMessage ?? "Invalid query parameters."]));
        }

        var unifiedQuery = _queryProcessor.OptimizeQuery(queryAdapterResult.Query.Value, resource);
        var unifiedQueryValidation = _queryProcessor.ValidateQuery(unifiedQuery, resource);
        if (!unifiedQueryValidation.IsValid)
        {
            return (null, null, StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [unifiedQueryValidation.ErrorMessage ?? "Invalid query parameters."]));
        }

        var query = _queryProcessor.ToFeatureQuery(unifiedQuery, resource) with
        {
            Where = validatedParams.Where,
            PublicIdAttributeName = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource)?.Name,
            // returnZ/returnM ask the storage read to carry the higher ordinates through the canonical
            // WKB (EWKB) so the converter can emit them; without this the 2D OGC WKB read path strips
            // Z/M before the converter's includeZ/includeM filter ever sees them (#1877).
            IncludeZ = validatedParams.ReturnZ,
            IncludeM = validatedParams.ReturnM
        };

        // Resolve gdbVersion to a branch VersionContext (#1272, ADR-0051). Absent / SDE.DEFAULT
        // resolves to DEFAULT, where the canonical read overlay is a no-op so the query SQL stays
        // byte-identical (CITE-protected). A named version is Enterprise-gated and Postgres-only.
        var (versionContext, versionError) = await FeatureServerVersioning.ResolveQueryVersionAsync(
            context, validatedParams.GdbVersion, cancellationToken).ConfigureAwait(false);
        if (versionError != null)
        {
            return (null, null, versionError);
        }

        if (versionContext is { IsDefault: false })
        {
            query = query with { VersionContext = versionContext };
        }

        // Resolve the datum transformation that drives the layerSR -> outSR reprojection.
        // Honors a client-supplied datumTransformation, otherwise applies the Esri default
        // for the pair; an unknown/unsupported request fails explicitly (never silent).
        var layerSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        var effectiveOutputSrid = query.OutputSrid ?? outputSrid;
        if (!TryResolveDatumTransformation(
                context,
                validatedParams.DatumTransformation,
                layerSrid,
                effectiveOutputSrid,
                out var datumSelection,
                out var datumError))
        {
            return (null, null, datumError);
        }

        if (datumSelection is not null)
        {
            query = query with { OutputDatumTransformation = datumSelection };
        }

        return (query, outputSrid, null);
    }

    /// <summary>
    /// Resolves the datum transformation for a layer-to-output reprojection. Honors a
    /// client-supplied <c>datumTransformation</c> WKID/composite when present; otherwise
    /// applies the catalog's Esri default for the SRID pair. An unknown or inapplicable
    /// client request yields an explicit Esri-style error rather than a silent substitute.
    /// </summary>
    private bool TryResolveDatumTransformation(
        HttpContext context,
        string? datumTransformationValue,
        int layerSrid,
        int? outputSrid,
        out DatumTransformationSelection? selection,
        out IResult? error)
    {
        selection = null;
        error = null;

        // No reprojection requested, or output equals the layer's own SRID: nothing to do.
        if (!outputSrid.HasValue || outputSrid.Value == layerSrid)
        {
            return true;
        }

        // Delegate the WKID -> PROJ pipeline selection to the shared resolver so the
        // FeatureServer, Geometry Service, and ImageServer project paths stay on one
        // catalog mapping; this method only adapts the neutral error into the
        // FeatureServer's problem-shape and emits the protocol log.
        if (!GeoServicesDatumTransformationResolver.TryResolve(
                _datumTransformationCatalog,
                datumTransformationValue,
                layerSrid,
                outputSrid.Value,
                out selection,
                out var resolveError))
        {
            error = StandardErrorHelpers.CreateBadRequest(context,
                "Invalid datumTransformation",
                [resolveError]);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(datumTransformationValue))
        {
            FeatureServerLog.DatumTransformationRequested(_logger, datumTransformationValue);
        }

        return true;
    }

    private static bool ShouldUseInternalObjectIdsFastPath(MetadataV2Resource resource)
        => GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource)?.Name.Equals(
            FieldNames.ObjectId,
            StringComparison.OrdinalIgnoreCase) != false;

    private static bool TryCreateObjectIdsExpression(
        MetadataV2Resource resource,
        IReadOnlyCollection<long> objectIds,
        out FilterExpression expression)
    {
        expression = null!;
        var objectIdField = GeoServicesObjectIdFieldResolver.ResolveObjectIdField(resource);
        if (objectIdField is null)
        {
            return false;
        }

        expression = new BinaryExpression(
            new PropertyReference(objectIdField.Name),
            BinaryOperator.In,
            new ValueList(objectIds
                .Distinct()
                .Select(static objectId => new Literal(objectId, LiteralType.Number))
                .ToArray()));
        return true;
    }

    private static bool IsNoOpFilterExpression(FilterExpression expression)
        => expression switch
        {
            Literal { Type: LiteralType.Boolean, Value: true } => true,
            BinaryExpression
            {
                Operator: BinaryOperator.Equal,
                Left: Literal left,
                Right: Literal right
            } => AreEquivalentLiteralValues(left, right),
            BinaryExpression
            {
                Operator: BinaryOperator.And,
                Left: var left,
                Right: var right
            } => IsNoOpFilterExpression(left) && IsNoOpFilterExpression(right),
            _ => false
        };

    private static bool AreEquivalentLiteralValues(Literal left, Literal right)
    {
        if (left.Type != right.Type)
        {
            return false;
        }

        return left.Type == LiteralType.Number
            ? string.Equals(
                Convert.ToString(left.Value, CultureInfo.InvariantCulture),
                Convert.ToString(right.Value, CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            : Equals(left.Value, right.Value);
    }

    /// <summary>
    /// Recovers a client-requested Web Mercator alias outSR (e.g. 102100) that the resolver
    /// normalized to the canonical <paramref name="outputSrid"/> (e.g. 3857), so the response
    /// spatialReference can echo <c>{wkid:102100, latestWkid:3857}</c> per Esri convention.
    /// Returns null when outSR was absent, already canonical, or not a Web Mercator alias of the
    /// resolved SRID (#2736).
    /// </summary>
    private async Task<int?> ResolveRequestedWebMercatorAliasAsync(
        QueryParameters validatedParams,
        int? outputSrid,
        CancellationToken cancellationToken)
    {
        if (!validatedParams.OutSrSpecified || !outputSrid.HasValue)
        {
            return null;
        }

        var rawOutSrid = await _queryServices.ParseSridAsync(validatedParams.OutSr, cancellationToken).ConfigureAwait(false);
        return rawOutSrid.HasValue
            && rawOutSrid.Value != outputSrid.Value
            && SpatialReferenceExtensions.NormalizeWebMercatorSrid(rawOutSrid.Value) == outputSrid.Value
            ? rawOutSrid.Value
            : null;
    }

    private async Task<QueryResponse> ExecuteJsonQueryResponseAsync(
        string serviceId,
        int layerId,
        QueryLayerContext queryLayer,
        QueryParameters validatedParams,
        FeatureQuery query,
        int? outputSrid,
        QueryLimits queryLimits,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
        {
            if (!TryParseStatisticsDefinitions(validatedParams.OutStatistics, queryLayer.Resource, out var statisticsDefs, out var statsError))
            {
                throw new ArgumentException(statsError ?? InvalidOutStatisticsJsonMessage);
            }

            ImmutableArray<string>? groupByFields = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.GroupByFieldsForStatistics))
            {
                if (!TryParseGroupByFields(validatedParams.GroupByFieldsForStatistics, queryLayer.Resource, out var parsedGroupBy, out var groupByError))
                {
                    throw new ArgumentException(groupByError ?? "groupByFieldsForStatistics must contain valid field names.");
                }

                groupByFields = parsedGroupBy;
            }

            ImmutableArray<HavingCondition>? havingConditions = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Having))
            {
                if (!TryParseHavingConditions(validatedParams.Having, statisticsDefs, queryLayer.Resource, out var parsedHaving, out var havingError))
                {
                    throw new ArgumentException(havingError ?? "having clause could not be parsed.");
                }

                havingConditions = parsedHaving;
            }

            var statisticsQuery = query with
            {
                OutStatistics = statisticsDefs,
                GroupByFields = groupByFields,
                Having = havingConditions,
                Limit = null,
                Offset = null,
                OrderBy = null,
                Distinct = false
            };

            var stopwatch = Stopwatch.StartNew();
            var statisticsRows = await _queryExecutor.QueryStatisticsAsync(
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                statisticsQuery,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "statistics", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

            var statisticsFeatures = statisticsRows.Select(row => new GeoServicesFeature
            {
                Attributes = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase),
                Geometry = null,
                // Aggregate rows have no geometry; omit the geometry key entirely (Esri parity).
                IncludeGeometry = false
            }).ToArray();

            return new QueryResponse { Features = statisticsFeatures };
        }

        var objectIdFieldName = GeoServicesObjectIdFieldResolver.ResolveObjectIdFieldName(queryLayer.Resource);

        if (validatedParams.ReturnCountOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            var count = await _queryExecutor.CountAsync(
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                query,
                cancellationToken).ConfigureAwait(false);
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

            // GetExtentAsync and CountAsync are independent reads over the same query
            // (neither depends on the other's result), so run them concurrently instead
            // of as two sequential round trips.
            var extentTask = _queryExecutor.GetExtentAsync(
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                query,
                cancellationToken);
            var extentCountTask = _queryExecutor.CountAsync(
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                query,
                cancellationToken);
            await Task.WhenAll(extentTask, extentCountTask).ConfigureAwait(false);
            var extent = extentTask.Result;
            var extentCount = extentCountTask.Result;
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "extent", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
            extent ??= await ResolveExtentFallbackAsync(context, validatedParams, queryLayer.Resource, outputSrid, cancellationToken).ConfigureAwait(false);
            return new QueryResponse
            {
                Extent = extent.HasValue ? extent.Value.ToExtentInfo() : null,
                Count = extentCount,
                Features = null
            };
        }

        if (validatedParams.ReturnIdsOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(
                queryLayer.Service,
                queryLayer.Resource,
                queryLayer.Publication,
                queryLayer.StorageLayerId,
                query,
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "ids", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);

            return new QueryResponse
            {
                ObjectIdFieldName = objectIdFieldName,
                ObjectIds = result.Items
                        .Select(feature => GeoServicesObjectIdFieldResolver.ResolveObjectIdValue(feature, objectIdFieldName))
                        .ToArray(),
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
        // DISTINCT is applied in memory, so bound the source-row scan at the
        // transfer limit (plus the requested window) instead of materializing
        // the whole layer; a truncated scan surfaces as exceededTransferLimit.
        var distinctScanLimit = ComputeDistinctScanLimit(query, queryLimits);
        var queryForExecution = shouldApplyDistinct
            ? query with { Limit = distinctScanLimit, Offset = null }
            : query;
        var queryStopwatch = Stopwatch.StartNew();
        QueryResult<Feature> queryResult = await _queryExecutor.QueryWithValidationAsync(
            queryLayer.Service,
            queryLayer.Resource,
            queryLayer.Publication,
            queryLayer.StorageLayerId,
            queryForExecution,
            cancellationToken).ConfigureAwait(false);
        queryStopwatch.Stop();
        FeatureServerLog.QueryExecuted(_logger, "query", serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

        if (shouldApplyDistinct)
        {
            var distinctScanTruncated = queryResult.HasMoreResults || queryResult.Items.Length >= distinctScanLimit;
            queryResult = ApplyDistinctValues(queryResult, outFields!);
            queryResult = ApplyPaginationWindow(queryResult, query.Offset, query.Limit, distinctScanTruncated);
        }

        var requestedOutputSrid = await ResolveRequestedWebMercatorAliasAsync(validatedParams, outputSrid, cancellationToken).ConfigureAwait(false);

        (object? formattedResponse, _) = await _queryServices.FormatQueryResultAsync(
            queryResult,
            queryLayer.Resource,
            "json",
            validatedParams.ReturnGeometry,
            outputSrid,
            validatedParams.ReturnZ,
            validatedParams.ReturnM,
            validatedParams.GeometryPrecision,
            validatedParams.MaxAllowableOffset,
            outFields,
            suppressObjectId: shouldApplyDistinct,
            returnCentroid: validatedParams.ReturnCentroid,
            requestedOutputSrid: requestedOutputSrid).ConfigureAwait(false);

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
        MetadataV2Resource resource,
        int? outputSrid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(queryParams);
        ArgumentNullException.ThrowIfNull(resource);

        var bbox = resource.ReadBbox();
        if (!CanFallbackToLayerExtent(queryParams) || bbox is null)
        {
            return null;
        }

        var layerExtent = FeatureExtent.Create(
            bbox.West,
            bbox.South,
            bbox.East,
            bbox.North,
            resource.ReadSrid() ?? SpatialReference.WGS84.Wkid);
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
        MetadataV2Resource resource,
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

        if (resource.ReadSrid() == srid)
        {
            return SpatialReference.Create(srid);
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

    // Parameters that change *result semantics* â€” rejecting them is correct because
    // silently ignoring would return output that differs from what the client asked for.
    //
    // Compatibility parameters that ArcGIS clients routinely send by default
    // (gdbVersion, quantizationParameters, datumTransformation, unknown resultType)
    // are silently accepted to preserve interop; a fail-closed response here would
    // break out-of-the-box connections from ArcGIS Pro and the JS API.
    private static bool TryValidateUnsupportedParameters(QueryParameters queryParams, out string? errorMessage)
    {
        var unsupported = new List<string>();

        // returnExceededLimitFeatures is accepted and ignored: Honua already reports
        // exceededTransferLimit when more rows exist within the page, so the flag needs no
        // special handling. The ArcGIS Maps SDK for .NET ServiceFeatureTable.QueryFeaturesAsync
        // always sends it, so rejecting it broke every .NET FeatureServer client (#1460).
        //
        // returnTrueCurves is accepted as a no-op on OUTPUT. True-curve INPUT (curvePaths/
        // curveRings on applyEdits or query geometry) is densified to linear vertices by
        // CurveGeometryConverter before storage (#1877 Part A), and NTS/WKB cannot represent a
        // true curve, so stored geometry is always linear and cannot be losslessly re-curved.
        // Honua therefore still advertises supportsTrueCurve=false and emits linearized geometry;
        // ArcGIS clients may still include the flag on otherwise ordinary feature queries.

        if (unsupported.Count == 0)
        {
            errorMessage = null;
            return true;
        }

        errorMessage = $"Unsupported query parameters: {string.Join(", ", unsupported)}.";
        return false;
    }

    /// <summary>
    /// Validates the Esri <c>sqlFormat</c> parameter. ArcGIS Pro and the SDKs send
    /// <c>standard</c> (SQL-92 standardized where syntax) or <c>native</c> (provider-native).
    /// Honua parses the where clause under its standardized ArcGIS-SQL dialect for both, so the
    /// value is accepted and honored; only an unrecognized token is rejected.
    /// </summary>
    private static bool TryValidateSqlFormat(QueryParameters queryParams, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(queryParams.SqlFormat))
        {
            return true;
        }

        var normalized = queryParams.SqlFormat.Trim();
        if (normalized.Equals("standard", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("native", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        errorMessage = "sqlFormat must be one of: standard, native.";
        return false;
    }

    private static bool TryParseStatisticsDefinitions(
        string outStatisticsJson,
        MetadataV2Resource resource,
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
                resource.SchemaFields.Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);
            var fieldTypes = new Dictionary<string, MetadataV2FieldType>(StringComparer.OrdinalIgnoreCase);
            foreach (var schemaField in resource.SchemaFields)
            {
                fieldTypes[schemaField.Name] = schemaField.Type;
            }

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

                // Numeric aggregates (sum/avg/stddev/var) are only meaningful over a
                // numeric column; applying them to a string field is a type mismatch
                // that the database would otherwise raise at execution time as a 500.
                // count/min/max are valid on any comparable type, so they are exempt.
                // ObjectId is always numeric, so its type need not be looked up.
                if (RequiresNumericField(statisticType) &&
                    fieldTypes.TryGetValue(onField, out var onFieldType) &&
                    !IsNumericFieldType(onFieldType))
                {
                    error = $"Statistic '{statisticTypeStr}' requires a numeric field; '{onField}' is not numeric.";
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

    // Parses a GeoServices `having` expression into structured aggregate
    // conditions. The grammar is deliberately narrow â€” `AGG(field) OP number`
    // terms joined by AND â€” so the provider can rebuild a fully parameterized
    // HAVING clause without ever concatenating client text into SQL. Each
    // aggregate must already be declared in outStatistics, mirroring the Esri
    // contract and reusing the same field-type hints as the SELECT aggregates.
    private static bool TryParseHavingConditions(
        string having,
        ImmutableArray<StatisticDefinition> statistics,
        MetadataV2Resource resource,
        out ImmutableArray<HavingCondition> conditions,
        out string? error)
    {
        error = null;
        conditions = default;

        var fieldTypes = new Dictionary<string, MetadataV2FieldType>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in resource.SchemaFields)
        {
            fieldTypes[field.Name] = field.Type;
        }

        var terms = HavingConjunctionRegex().Split(having);
        var parsed = new List<HavingCondition>();

        foreach (var term in terms.Select(rawTerm => rawTerm.Trim()))
        {
            if (term.Length == 0)
            {
                error = "having must contain one or more 'AGG(field) <op> value' conditions.";
                return false;
            }

            var match = HavingTermRegex().Match(term);
            if (!match.Success)
            {
                error = $"Unsupported having condition: '{term}'. Expected 'AGG(field) <op> number' where AGG is one of count, sum, min, max, avg, stddev, var.";
                return false;
            }

            if (!TryParseStatisticType(match.Groups["agg"].Value, out var statisticType))
            {
                error = $"Unsupported aggregate function in having: '{match.Groups["agg"].Value}'.";
                return false;
            }

            var field = match.Groups["field"].Value;
            if (!fieldTypes.ContainsKey(field) &&
                !string.Equals(field, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Field '{field}' referenced in having does not exist on the layer.";
                return false;
            }

            // The aggregate referenced by HAVING must also be requested in
            // outStatistics; ArcGIS clients always pair the two and it lets us
            // reuse the resolved field-type hint for correct numeric casting.
            var matchingStat = statistics.FirstOrDefault(s =>
                s.StatisticType == statisticType &&
                string.Equals(s.OnStatisticField, field, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(matchingStat.OnStatisticField))
            {
                error = $"having condition '{term}' references an aggregate that is not declared in outStatistics.";
                return false;
            }

            if (!TryParseHavingOperator(match.Groups["op"].Value, out var op))
            {
                error = $"Unsupported comparison operator in having: '{match.Groups["op"].Value}'.";
                return false;
            }

            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                error = $"Invalid numeric value in having condition: '{term}'.";
                return false;
            }

            var fieldType = fieldTypes.TryGetValue(field, out var resolvedType)
                ? resolvedType
                : (MetadataV2FieldType?)null;

            parsed.Add(new HavingCondition
            {
                StatisticType = statisticType,
                OnStatisticField = field,
                Operator = op,
                Value = value,
                FieldType = fieldType
            });
        }

        if (parsed.Count == 0)
        {
            error = "having must contain one or more 'AGG(field) <op> value' conditions.";
            return false;
        }

        conditions = parsed.ToImmutableArray();
        return true;
    }

    private static bool TryParseHavingOperator(string value, out HavingComparisonOperator op)
    {
        switch (value)
        {
            case "=":
                op = HavingComparisonOperator.Equal;
                return true;
            case "<>":
            case "!=":
                op = HavingComparisonOperator.NotEqual;
                return true;
            case ">":
                op = HavingComparisonOperator.GreaterThan;
                return true;
            case ">=":
                op = HavingComparisonOperator.GreaterThanOrEqual;
                return true;
            case "<":
                op = HavingComparisonOperator.LessThan;
                return true;
            case "<=":
                op = HavingComparisonOperator.LessThanOrEqual;
                return true;
            default:
                op = default;
                return false;
        }
    }

    // Parses groupByFieldsForStatistics and validates each name against the
    // layer schema, mirroring the onStatisticField/outFields/orderByFields
    // existence checks. An unvalidated groupBy column would otherwise reach the
    // database and surface as a 500 instead of a structured 400.
    private static bool TryParseGroupByFields(
        string groupByFieldsForStatistics,
        MetadataV2Resource resource,
        out ImmutableArray<string> groupByFields,
        out string? error)
    {
        error = null;
        groupByFields = default;

        var parsed = groupByFieldsForStatistics
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim())
            .Where(f => f.Length > 0)
            .ToImmutableArray();
        if (parsed.IsDefaultOrEmpty)
        {
            error = "groupByFieldsForStatistics must contain valid field names.";
            return false;
        }

        var fieldNames = new HashSet<string>(
            resource.SchemaFields.Select(f => f.Name),
            StringComparer.OrdinalIgnoreCase)
        {
            FieldNames.ObjectId
        };

        var missingField = parsed.FirstOrDefault(field => !fieldNames.Contains(field));
        if (missingField != null)
        {
            error = $"Field '{missingField}' referenced in groupByFieldsForStatistics does not exist on the layer.";
            return false;
        }

        groupByFields = parsed;
        return true;
    }

    // Aggregates that compute over numeric magnitudes; applying them to a
    // non-numeric column is a type mismatch. min/max/count operate on any
    // comparable type and are intentionally excluded.
    private static bool RequiresNumericField(StatisticType statisticType) =>
        statisticType is StatisticType.Sum
            or StatisticType.Avg
            or StatisticType.Stddev
            or StatisticType.Var;

    private static bool IsNumericFieldType(MetadataV2FieldType fieldType) =>
        fieldType is MetadataV2FieldType.Integer
            or MetadataV2FieldType.BigInteger
            or MetadataV2FieldType.Double
            or MetadataV2FieldType.Float;

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

    // A SQL "data exception" (SQLSTATE class 22) is raised by the database when a
    // value in the executed statement is malformed or out of range â€” division/mod by
    // zero (22012/22020), numeric overflow (22003), invalid argument to LOG/POWER/SQRT
    // (2201E/2201F/2201G), bad text-to-number CAST (22P02), etc. For a read query these
    // are always driven by client `where`/arithmetic input, so they are client errors
    // (400), not server faults (500). DbException.SqlState is provider-agnostic, so this
    // adapter stays free of any concrete provider dependency.
    private static bool IsClientDataException(DbException exception)
    {
        var sqlState = exception.SqlState;
        return !string.IsNullOrEmpty(sqlState)
            && sqlState.Length >= 2
            && sqlState.StartsWith("22", StringComparison.Ordinal);
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
            || exception.Message.Contains("Geometry is required for nearest neighbor queries", StringComparison.OrdinalIgnoreCase)
            // FeatureProviderQueryRouter raises "Feature provider 'X' does not support Y operations." when
            // the resolved provider's capability bit is false (e.g. MySQL/MariaDB outStatistics, DuckDB
            // edits). The router throws InvalidOperationException rather than NotSupportedException, so
            // the message-prefix check is required to keep these capability mismatches out of the 500 path.
            || exception.Message.Contains("does not support", StringComparison.OrdinalIgnoreCase);
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
        => string.Equals(requiredProtocol, MapServerProtocol, StringComparison.OrdinalIgnoreCase)
            ? HonuaTelemetry.Protocols.MapServer
            : HonuaTelemetry.Protocols.FeatureServer;

    // Computes the bounded number of source rows to scan for an in-memory
    // returnDistinctValues evaluation: the configured transfer limit (or the
    // requested window when it is larger) plus one row so a truncated scan can
    // be detected and reported as exceededTransferLimit.
    //
    // BH2-001: Cap at MaxRecordCount * 2 + 1 so a caller with a very large
    // resultOffset cannot force a multi-million-row materialization. A request
    // whose offset+limit window exceeds this cap will still see a valid (possibly
    // empty) page plus exceededTransferLimit=true â€” it cannot force an OOM.
    private static int ComputeDistinctScanLimit(FeatureQuery query, QueryLimits queryLimits)
    {
        var requestedWindow = Math.Max(0, query.Offset ?? 0) + Math.Max(0, query.Limit ?? 0);
        var uncapped = Math.Max(queryLimits.MaxRecordCount, requestedWindow) + 1;
        var cap = queryLimits.MaxRecordCount * 2 + 1;
        return Math.Min(uncapped, cap);
    }

    private static QueryResult<Feature> ApplyPaginationWindow(
        QueryResult<Feature> result,
        int? offset,
        int? limit,
        bool scanTruncated = false)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return scanTruncated
                ? result with { HasMoreResults = true }
                : result;
        }

        var totalCount = result.Items.Length;
        var effectiveOffset = Math.Max(0, offset ?? 0);
        if (effectiveOffset >= totalCount)
        {
            return QueryResult<Feature>.Create(totalCount, ImmutableArray<Feature>.Empty, scanTruncated);
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
        var hasMore = scanTruncated || effectiveOffset + take < totalCount;

        return QueryResult<Feature>.Create(totalCount, pageItems, hasMore);
    }

    // Rebuilds the Esri json featureSet with quantized (integer grid, delta-encoded)
    // geometry coordinates and the matching transform, when quantizationParameters was
    // requested. The transform lets clients recover world coordinates.
    private static QueryResponse ApplyQuantization(QueryResponse response, QuantizationTransform transform)
    {
        GeoServicesFeature[]? features = response.Features;
        if (features is { Length: > 0 })
        {
            var quantized = new GeoServicesFeature[features.Length];
            for (var i = 0; i < features.Length; i++)
            {
                var feature = features[i];
                quantized[i] = new GeoServicesFeature
                {
                    Attributes = feature.Attributes,
                    Geometry = feature.Geometry is { } geometry ? FeatureQuantizer.Quantize(geometry, transform) : null,
                    Centroid = feature.Centroid is { } centroid ? FeatureQuantizer.Quantize(centroid, transform) : null,
                    IncludeGeometry = feature.IncludeGeometry,
                };
            }

            features = quantized;
        }

        return new QueryResponse
        {
            GeometryType = response.GeometryType,
            SpatialReference = response.SpatialReference,
            DisplayFieldName = response.DisplayFieldName,
            Fields = response.Fields,
            HasZ = response.HasZ,
            HasM = response.HasM,
            ObjectIdFieldName = response.ObjectIdFieldName,
            ObjectIds = response.ObjectIds,
            Count = response.Count,
            Extent = response.Extent,
            UniqueIdField = response.UniqueIdField,
            GlobalIdFieldName = response.GlobalIdFieldName,
            Features = features,
            ExceededTransferLimit = response.ExceededTransferLimit,
            Transform = new GeoServicesTransform
            {
                OriginPosition = transform.OriginPosition,
                Scale = [transform.ScaleX, transform.ScaleY],
                Translate = [transform.TranslateX, transform.TranslateY],
            },
        };
    }

    private static string BuildDistinctKey(Feature feature, string[] outFields)
    {
        var parts = new string[outFields.Length];
        for (var i = 0; i < outFields.Length; i++)
        {
            var fieldName = outFields[i];
            parts[i] = feature.Attributes.TryGetValue(fieldName, out var value) && value != null
                ? value switch
                {
                    IConvertible convertible => convertible.ToString(CultureInfo.InvariantCulture),
                    IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                    _ => value.ToString() ?? string.Empty
                }
                : "\0null\0";
        }

        return string.Join("\0", parts);
    }

    // Splits a having expression on the boolean AND conjunction (case-insensitive,
    // word-bounded). OR is intentionally unsupported; aggregate group filters are
    // conjunctive in the Esri contract.
    [GeneratedRegex(@"\s+AND\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HavingConjunctionRegex();

    // Matches a single 'AGG(field) <op> number' term. The field token is a bare
    // SQL identifier and the value is a plain decimal literal, so no client text
    // can reach the SQL string beyond the validated aggregate/field/operator.
    [GeneratedRegex(
        @"^(?<agg>count|sum|min|max|avg|stddev|var)\s*\(\s*(?<field>[A-Za-z_][A-Za-z0-9_]*)\s*\)\s*(?<op>!=|<>|<=|>=|=|<|>)\s*(?<value>-?\d+(?:\.\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HavingTermRegex();
}

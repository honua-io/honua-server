// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Parsing;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer query operations.
/// </summary>
internal sealed class FeatureServerQueryHandler(
    FeatureServerQueryDependencies dependencies,
    ILogger<FeatureServerQueryHandler> logger) : IFeatureQueryDispatcher
{
    private static readonly IResult _streamingResult = new StreamingResult();
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureServerQueryServices _queryServices = dependencies.QueryServices;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly FeatureServerQueryExecutor _queryExecutor = dependencies.QueryExecutor;
    private readonly IResponseCache _responseCache = dependencies.ResponseCache;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly CacheOptions _cacheOptions = dependencies.CacheOptions;
    private readonly ILogger<FeatureServerQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private const int StreamingThreshold = 1000;

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
    {
        Activity? featureActivity = null;
        try
        {
            FeatureServerLog.QueryRequested(_logger, serviceId, layerId, queryParams.Where);

            var resourceValidationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
                _resourceValidator,
                serviceId,
                layerId,
                context,
                _logger,
                cancellationToken);
            if (!resourceValidationResult.IsValid)
            {
                return resourceValidationResult.ErrorResult!;
            }

            ServiceDefinition service = resourceValidationResult.Service!;
            LayerDefinition layer = resourceValidationResult.Layer!;
            var requestActivity = Activity.Current;
            requestActivity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.FeatureServer);
            requestActivity?.SetTag(HonuaTelemetry.Tags.ServiceId, serviceId);
            requestActivity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId.ToString(CultureInfo.InvariantCulture));

            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
            if (accessError != null)
            {
                return accessError;
            }

            featureActivity = HonuaTelemetry.StartFeatureActivity(
                "query",
                HonuaTelemetry.Protocols.FeatureServer,
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
            QueryValidationResult queryValidationResult = _queryServices.ValidateQueryLimits(queryParams);
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

            if (!FeatureServerEndpoints.TryValidateOutputFormat(
                validatedParams.F,
                FeatureServerEndpoints.FeatureServerQueryFormats,
                out var format,
                out var formatError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid query parameters",
                    [formatError ?? "Output format is not supported."]);
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

            GeoServicesGeometry? parsedGeometry = null;
            if (!FeatureServerGeometryParser.TryParseGeoServicesGeometry(validatedParams.Geometry, validatedParams.GeometryType, out parsedGeometry, out var geometryError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidGeometryParameter,
                    [geometryError ?? "Geometry parameter is invalid."]);
            }

            var inputSrid = await _queryServices.ResolveSridAsync(validatedParams.InSr, parsedGeometry?.SpatialReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.InSr) && !inputSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid input spatial reference",
                    [$"Unsupported inSR value: {validatedParams.InSr}"]);
            }

            if (parsedGeometry != null && !inputSrid.HasValue)
            {
                inputSrid = layer.SpatialReference.ToSrid();
            }

            if (parsedGeometry != null && inputSrid.HasValue && queryLimits.MaxBboxAreaSqKm.HasValue)
            {
                var areaLimitResult = ValidateBboxAreaLimit(parsedGeometry, inputSrid.Value, queryLimits.MaxBboxAreaSqKm.Value);
                if (!areaLimitResult.IsValid)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Query parameters exceed configured limits",
                        [areaLimitResult.ErrorMessage!]);
                }
            }

            var outputSrid = await _queryServices.ResolveSridAsync(validatedParams.OutSr, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.OutSr) && !outputSrid.HasValue)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Invalid output spatial reference",
                    [$"Unsupported outSR value: {validatedParams.OutSr}"]);
            }

            var requiresGeoJsonOutput = string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase)
                && !validatedParams.ReturnCountOnly
                && !validatedParams.ReturnExtentOnly
                && !validatedParams.ReturnIdsOnly;

            if (requiresGeoJsonOutput)
            {
                var wgs84Srid = SpatialReference.WGS84.Wkid;
                if (outputSrid.HasValue && outputSrid.Value != wgs84Srid)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "GeoJSON output only supports EPSG:4326 (WGS84).");
                }

                outputSrid ??= wgs84Srid;
            }

            FilterExpression? filterExpression = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Where))
            {
                var parseResult = _filterExpressionService.Parse(FilterLanguage.ArcGisSql, validatedParams.Where);
                if (!parseResult.IsSuccess)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [parseResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                filterExpression = parseResult.Expression;
                if (filterExpression != null && !FilterExpressionHelpers.IsBooleanFilterExpression(filterExpression))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        ["Invalid where clause."]);
                }
            }

            FilterExpression? temporalExpression = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Time))
            {
                try
                {
                    temporalExpression = FeatureServerTemporalQueryBuilder.BuildTemporalExpression(validatedParams, layer);
                }
                catch (ArgumentException ex)
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [$"Invalid time parameter: {ex.Message}"]);
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
                    return StandardErrorHelpers.CreateBadRequest(context,
                        ErrorMessages.Validation.InvalidParameter,
                        [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                }

                sqlFilter = translationResult.SqlFilter;
            }

            // Build query from validated parameters
            FeatureQuery query = BuildFeatureQuery(validatedParams, layer, parsedGeometry, inputSrid, outputSrid, sqlFilter, queryLimits);

            // Handle statistics queries (outStatistics)
            if (!string.IsNullOrWhiteSpace(validatedParams.OutStatistics))
            {
                if (!TryParseStatisticsDefinitions(validatedParams.OutStatistics, layer, out var statisticsDefs, out var statsError))
                {
                    return StandardErrorHelpers.CreateBadRequest(context,
                        "Invalid outStatistics",
                        [statsError ?? "outStatistics must be a valid JSON array."]);
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

            var objectIdFieldName = layer.PrimaryKeyField?.Name ?? FieldNames.ObjectId;

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
            var useStreaming = effectiveLimit > StreamingThreshold;

            if (!useStreaming)
            {
                var cached = await TryGetCachedResponseAsync();
                if (cached != null)
                {
                    return cached;
                }

                var queryStopwatch = Stopwatch.StartNew();
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(layerId, query, cancellationToken);
                queryStopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "query", serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

                string[]? outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                    [.. validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

                if (validatedParams.ReturnDistinctValues && outFields is { Length: > 0 })
                {
                    result = ApplyDistinctValues(result, outFields);
                }

                (object? formattedResponse, string? contentType) = _queryServices.FormatQueryResult(
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
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);
            HonuaTelemetry.RecordException(featureActivity, ex);

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            // Return safe error message without leaking exception details
            return StandardErrorHelpers.CreateBadRequest(context, ErrorMessages.Validation.InvalidParameter);
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

    private sealed class StreamingResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    /// <summary>
    /// Builds a FeatureQuery from query parameters
    /// </summary>
    private FeatureQuery BuildFeatureQuery(
        QueryParameters queryParams,
        LayerDefinition layer,
        GeoServicesGeometry? parsedGeometry,
        int? inputSrid,
        int? outputSrid,
        SqlFragment? sqlFilter,
        QueryLimits queryLimits)
    {
        var hasObjectIds = queryParams.ObjectIds is { Length: > 0 };
        var effectiveSqlFilter = hasObjectIds ? null : sqlFilter;
        var effectiveWhere = hasObjectIds ? null : queryParams.Where;

        var query = new FeatureQuery
        {
            Where = effectiveWhere,
            SqlFilter = effectiveSqlFilter,
            ObjectIds = hasObjectIds ? queryParams.ObjectIds?.ToImmutableArray() : null,
            Offset = queryParams.ResultOffset,
            Limit = hasObjectIds
                ? queryParams.ResultRecordCount ?? queryParams.ObjectIds?.Length
                : queryParams.ResultRecordCount ?? queryLimits.DefaultRecordCount,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid,
            Distinct = queryParams.ReturnDistinctValues,
            OrderBy = OrderByParsing.ParseFeatureServerOrderBy(
                queryParams.OrderByFields,
                layer,
                FeatureServerOrderByFields.AllowedCoreOrderByFields)
        };

        // Parse outFields if specified
        if (!string.IsNullOrEmpty(queryParams.OutFields))
        {
            if (queryParams.OutFields == "*")
            {
                // Return all fields - let the query run without field filtering
                query = query with { OutFields = null };
            }
            else
            {
                var fields = queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(f => f.Trim())
                    .ToImmutableArray();
                query = query with { OutFields = fields };
            }
        }

        // Parse spatial filter if specified (geometry or NearestCount)
        if (parsedGeometry != null || queryParams.NearestCount.HasValue)
        {
            try
            {
                // For KNN queries without explicit geometry, we need a geometry - use a default point if not provided
                if (queryParams.NearestCount.HasValue && parsedGeometry == null)
                {
                    throw new InvalidOperationException("Geometry is required for nearest neighbor queries");
                }

                SpatialFilter spatialFilter = FeatureServerSpatialFilterBuilder.BuildSpatialFilter(
                    queryParams,
                    parsedGeometry!,
                    inputSrid);
                query = query with { SpatialFilter = spatialFilter };
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid spatial parameters: {ex.Message}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Invalid geometry: {ex.Message}");
            }
        }

        return query;
    }

    private static ValidationResult ValidateBboxAreaLimit(
        GeoServicesGeometry geometry,
        int srid,
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

        var bboxAreaSqKm = CalculateBoundingBoxAreaSqKm(minX, minY, maxX, maxY, srid);
        if (bboxAreaSqKm <= maxBboxAreaSqKm)
        {
            return ValidationResult.Success();
        }

        return ValidationResult.Failure(
            $"Geometry bounding box area ({bboxAreaSqKm.ToString("F2", CultureInfo.InvariantCulture)} sq km) exceeds maximum allowed area ({maxBboxAreaSqKm.ToString("F2", CultureInfo.InvariantCulture)} sq km).");
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

    private static double CalculateBoundingBoxAreaSqKm(double minX, double minY, double maxX, double maxY, int srid)
    {
        if (IsGeographicSrid(srid))
        {
            return CalculateGeographicAreaSqKm(minX, minY, maxX, maxY);
        }

        var width = Math.Abs(maxX - minX);
        var height = Math.Abs(maxY - minY);
        return width * height / 1_000_000.0;
    }

    private static double CalculateGeographicAreaSqKm(double minLon, double minLat, double maxLon, double maxLat)
    {
        const double EarthRadiusKm = 6371.0088;

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
        return EarthRadiusKm * EarthRadiusKm * sphericalBand * longitudeSpanRad;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static bool IsGeographicSrid(int srid)
        => srid is 4326 or 4269 or 4267 or (>= 4000 and <= 4999);

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

        if (!string.IsNullOrWhiteSpace(queryParams.ResultType) &&
            !string.Equals(queryParams.ResultType, "standard", StringComparison.OrdinalIgnoreCase))
        {
            unsupported.Add("resultType");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.Having))
        {
            unsupported.Add("having");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.SqlFormat))
        {
            unsupported.Add("sqlFormat");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.GdbVersion))
        {
            unsupported.Add("gdbVersion");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.QuantizationParameters))
        {
            unsupported.Add("quantizationParameters");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.DatumTransformation))
        {
            unsupported.Add("datumTransformation");
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
        catch (JsonException ex)
        {
            error = $"Invalid outStatistics JSON: {ex.Message}";
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

    private static string BuildDistinctKey(Feature feature, string[] outFields)
    {
        var parts = new string[outFields.Length];
        for (var i = 0; i < outFields.Length; i++)
        {
            var fieldName = outFields[i];
            if (feature.Attributes.TryGetValue(fieldName, out var value) && value != null)
            {
                parts[i] = value.ToString() ?? string.Empty;
            }
            else
            {
                parts[i] = "\0null\0";
            }
        }

        return string.Join("\0", parts);
    }
}

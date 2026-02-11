// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Parsing;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer query operations.
/// </summary>
internal sealed class FeatureServerQueryHandler(
    FeatureServerQueryDependencies dependencies,
    ILogger<FeatureServerQueryHandler> logger)
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
                if (filterExpression != null && !IsBooleanFilterExpression(filterExpression))
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
                var idsEffectiveLimit = query.Limit ?? queryLimits.DefaultRecordCount;
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

            var effectiveLimit = query.Limit ?? queryLimits.DefaultRecordCount;
            var useStreaming = effectiveLimit > StreamingThreshold;
            if (validatedParams.ReturnDistinctValues)
            {
                // Distinct handling requires materialized results.
                useStreaming = false;
            }

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

                if (validatedParams.ReturnDistinctValues)
                {
                    result = ApplyDistinctValues(result, query.OutFields);
                }

                string[]? outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                    [.. validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

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
            Limit = queryParams.ResultRecordCount ?? queryLimits.DefaultRecordCount,
            SpatialReferenceSrid = layer.SpatialReference.ToSrid(),
            OutputSrid = outputSrid,
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

    private static bool IsBooleanFilterExpression(FilterExpression expression)
    {
        return expression switch
        {
            BinaryExpression => true,
            UnaryExpression => true,
            SpatialPredicate => true,
            SpatialDistancePredicate => true,
            TemporalPredicate => true,
            ArrayPredicate => true,
            Literal literal => literal.Type == LiteralType.Boolean,
            _ => false
        };
    }


    private static QueryResult<Feature> ApplyDistinctValues(
        QueryResult<Feature> result,
        ImmutableArray<string>? outFields)
    {
        if (result.Items.IsDefaultOrEmpty)
        {
            return result;
        }

        var distinctItems = new List<Feature>(result.Items.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var feature in result.Items)
        {
            var key = BuildDistinctKey(feature, outFields);
            if (seen.Add(key))
            {
                distinctItems.Add(feature);
            }
        }

        return QueryResult<Feature>.Create(distinctItems.Count, distinctItems.ToImmutableArray(), result.HasMoreResults);
    }

    private static string BuildDistinctKey(Feature feature, ImmutableArray<string>? outFields)
    {
        IEnumerable<string> fieldNames = outFields.HasValue && !outFields.Value.IsDefaultOrEmpty
            ? outFields.Value
            : feature.Attributes.Keys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        foreach (var field in fieldNames)
        {
            builder.Append(field.ToLowerInvariant());
            builder.Append('=');
            feature.Attributes.TryGetValue(field, out var value);
            builder.Append(FormatDistinctValue(value));
            builder.Append('|');
        }

        return builder.ToString();
    }

    private static string FormatDistinctValue(object? value)
    {
        if (value == null)
        {
            return "<null>";
        }

        return value switch
        {
            string text => text,
            JsonElement element => element.GetRawText(),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
    }

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

        if (!string.IsNullOrWhiteSpace(queryParams.OutStatistics))
        {
            unsupported.Add("outStatistics");
        }

        if (!string.IsNullOrWhiteSpace(queryParams.GroupByFieldsForStatistics))
        {
            unsupported.Add("groupByFieldsForStatistics");
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
}

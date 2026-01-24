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
    private static readonly char[] _coordinateSeparators = { ',', ' ' };
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

            var format = validatedParams.F ?? "json";
            if (string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Output format 'pbf' is not supported");
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
            if (!TryParseGeoServicesGeometry(validatedParams.Geometry, validatedParams.GeometryType, out parsedGeometry, out var geometryError))
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
                    temporalExpression = BuildTemporalExpression(validatedParams, layer);
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
                    validatedParams.F ?? "json",
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

    private enum TimeRelation
    {
        Intersects,
        Overlaps,
        Within,
        Contains,
        Disjoint,
        Before,
        After,
        Equals,
        Starts,
        StartedBy,
        Finishes,
        FinishedBy,
        Meets,
        MetBy,
        OverlapsStartWithinEnd,
        OverlapsEndWithinStart
    }

    /// <summary>
    /// Builds a temporal filter expression for FeatureServer time queries.
    /// </summary>
    private static FilterExpression? BuildTemporalExpression(QueryParameters queryParams, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(queryParams.Time))
        {
            return null;
        }

        var selection = TemporalExtentHelpers.ResolveTemporalFieldsOrThrow(layer);
        if (!TryParseTimeParameter(queryParams.Time, out var startTime, out var endTime))
        {
            throw new ArgumentException($"Invalid time parameter format: {queryParams.Time}");
        }

        var relation = ParseTimeRelation(queryParams.TimeRelation);
        var temporalType = selection.StartField.Type;
        var queryStart = ToTemporalLiteral(startTime, temporalType);
        var queryEnd = ToTemporalLiteral(endTime, temporalType);

        var startExpression = new PropertyReference(selection.StartField.Name);
        FilterExpression endExpression = selection.EndField == null
            ? startExpression
            : new FunctionCall(
                "COALESCE",
                new FilterExpression[]
                {
                    new PropertyReference(selection.EndField.Name),
                    startExpression
                });

        return BuildTemporalRelationExpression(relation, startExpression, endExpression, queryStart, queryEnd);
    }

    private static TimeRelation ParseTimeRelation(string? timeRelation)
    {
        if (string.IsNullOrWhiteSpace(timeRelation))
        {
            return TimeRelation.Intersects;
        }

        return timeRelation.Trim().ToLowerInvariant() switch
        {
            "esritimerelationintersects" or "intersects" => TimeRelation.Intersects,
            "esritimerelationoverlaps" or "overlaps" => TimeRelation.Overlaps,
            "esritimerelationwithin" or "within" => TimeRelation.Within,
            "esritimerelationcontains" or "contains" => TimeRelation.Contains,
            "esritimerelationdisjoint" or "disjoint" => TimeRelation.Disjoint,
            "esritimerelationbefore" or "before" => TimeRelation.Before,
            "esritimerelationafter" or "after" => TimeRelation.After,
            "esritimerelationequals" or "equals" => TimeRelation.Equals,
            "esritimerelationstarts" or "starts" => TimeRelation.Starts,
            "esritimerelationstartedby" or "startedby" => TimeRelation.StartedBy,
            "esritimerelationfinishes" or "finishes" => TimeRelation.Finishes,
            "esritimerelationfinishedby" or "finishedby" => TimeRelation.FinishedBy,
            "esritimerelationmeets" or "meets" => TimeRelation.Meets,
            "esritimerelationmetby" or "metby" => TimeRelation.MetBy,
            "esritimerelationoverlapsstartwithinend" or "overlapsstartwithinend" => TimeRelation.OverlapsStartWithinEnd,
            "esritimerelationoverlapsendwithinstart" or "overlapsendwithinstart" => TimeRelation.OverlapsEndWithinStart,
            _ => throw new ArgumentException($"Unsupported timeRelation '{timeRelation}'.")
        };
    }

    private static FilterExpression? BuildTemporalRelationExpression(
        TimeRelation relation,
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd)
    {
        var startLessThan = Compare(endExpression, BinaryOperator.LessThan, queryStart);
        var startGreaterThan = Compare(startExpression, BinaryOperator.GreaterThan, queryEnd);
        var disjoint = Or(startLessThan, startGreaterThan);

        return relation switch
        {
            TimeRelation.Intersects => disjoint == null ? null : new UnaryExpression(UnaryOperator.Not, disjoint),
            TimeRelation.Disjoint => disjoint,
            TimeRelation.Before => CompareRequired(endExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
            TimeRelation.After => CompareRequired(startExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
            TimeRelation.Equals => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                relation),
            TimeRelation.Contains => AndRequired(
                CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Within => AndRequired(
                CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Starts => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.StartedBy => AndRequired(
                CompareRequired(startExpression, BinaryOperator.Equal, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            TimeRelation.Finishes => AndRequired(
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                relation),
            TimeRelation.FinishedBy => AndRequired(
                CompareRequired(endExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
                CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
                relation),
            TimeRelation.Meets => CompareRequired(endExpression, BinaryOperator.Equal, queryStart, relation, "start"),
            TimeRelation.MetBy => CompareRequired(startExpression, BinaryOperator.Equal, queryEnd, relation, "end"),
            TimeRelation.Overlaps => Or(
                BuildOverlapStartWithinEnd(startExpression, endExpression, queryStart, queryEnd, relation),
                BuildOverlapEndWithinStart(startExpression, endExpression, queryStart, queryEnd, relation)),
            TimeRelation.OverlapsStartWithinEnd => BuildOverlapStartWithinEnd(startExpression, endExpression, queryStart, queryEnd, relation),
            TimeRelation.OverlapsEndWithinStart => BuildOverlapEndWithinStart(startExpression, endExpression, queryStart, queryEnd, relation),
            _ => throw new ArgumentException($"Unsupported timeRelation '{relation}'.")
        };
    }

    private static BinaryExpression? BuildOverlapStartWithinEnd(
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd,
        TimeRelation relation)
    {
        return AndRequired(
            CompareRequired(startExpression, BinaryOperator.LessThan, queryStart, relation, "start"),
            AndRequired(
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
                CompareRequired(endExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                relation),
            relation);
    }

    private static BinaryExpression? BuildOverlapEndWithinStart(
        FilterExpression startExpression,
        FilterExpression endExpression,
        Literal? queryStart,
        Literal? queryEnd,
        TimeRelation relation)
    {
        return AndRequired(
            CompareRequired(startExpression, BinaryOperator.GreaterThan, queryStart, relation, "start"),
            AndRequired(
                CompareRequired(startExpression, BinaryOperator.LessThan, queryEnd, relation, "end"),
                CompareRequired(endExpression, BinaryOperator.GreaterThan, queryEnd, relation, "end"),
                relation),
            relation);
    }

    private static BinaryExpression? Compare(FilterExpression left, BinaryOperator op, Literal? right)
    {
        if (right == null)
        {
            return null;
        }

        return new BinaryExpression(left, op, right);
    }

    private static BinaryExpression CompareRequired(
        FilterExpression left,
        BinaryOperator op,
        Literal? right,
        TimeRelation relation,
        string requiredPart)
    {
        if (right == null)
        {
            throw new ArgumentException($"timeRelation '{relation}' requires a {requiredPart} time value.");
        }

        return new BinaryExpression(left, op, right);
    }

    private static BinaryExpression AndRequired(FilterExpression left, FilterExpression right, TimeRelation relation)
    {
        _ = relation;
        return new BinaryExpression(left, BinaryOperator.And, right);
    }

    private static FilterExpression? Or(FilterExpression? left, FilterExpression? right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        return new BinaryExpression(left, BinaryOperator.Or, right);
    }

    private static Literal? ToTemporalLiteral(DateTimeOffset? value, FieldType fieldType)
    {
        if (!value.HasValue)
        {
            return null;
        }

        if (fieldType == FieldType.Date)
        {
            return new Literal(DateOnly.FromDateTime(value.Value.UtcDateTime), LiteralType.Date);
        }

        return new Literal(value.Value, LiteralType.DateTime);
    }

    /// <summary>
    /// Parses time parameter string into start/end times.
    /// Supports Unix timestamps in milliseconds, ISO 8601, and open intervals using null/empty.
    /// </summary>
    private static bool TryParseTimeParameter(string timeParam, out DateTimeOffset? start, out DateTimeOffset? end)
    {
        start = null;
        end = null;

        if (string.IsNullOrWhiteSpace(timeParam))
        {
            return false;
        }

        if (timeParam.Contains(','))
        {
            var parts = timeParam.Split(',', 2, StringSplitOptions.None);
            if (parts.Length != 2)
            {
                return false;
            }

            if (!TryParseOptionalTime(parts[0].Trim(), out start))
            {
                return false;
            }

            if (!TryParseOptionalTime(parts[1].Trim(), out end))
            {
                return false;
            }

            if (!start.HasValue && !end.HasValue)
            {
                return false;
            }

            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                return false;
            }

            return true;
        }

        if (!TryParseSingleTime(timeParam, out start))
        {
            return false;
        }

        end = start;
        return true;
    }

    private static bool TryParseOptionalTime(string timeValue, out DateTimeOffset? time)
    {
        time = null;

        if (string.IsNullOrWhiteSpace(timeValue) ||
            string.Equals(timeValue, "null", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParseSingleTime(timeValue, out time);
    }

    /// <summary>
    /// Parses a single time value (Unix timestamp or ISO 8601)
    /// </summary>
    private static bool TryParseSingleTime(string timeValue, out DateTimeOffset? time)
    {
        time = null;

        if (string.IsNullOrWhiteSpace(timeValue))
        {
            return false;
        }

        if (long.TryParse(timeValue, out var unixMs))
        {
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (DateTimeOffset.TryParse(timeValue, out var parsedTime))
        {
            time = parsedTime;
            return true;
        }

        return false;
    }

    private static bool TryParseGeoServicesGeometry(
        string? geometryText,
        string? geometryType,
        out GeoServicesGeometry? geometry,
        out string? error)
    {
        geometry = null;
        error = null;

        if (string.IsNullOrWhiteSpace(geometryText))
        {
            return true;
        }

        var trimmed = geometryText.Trim();
        if (trimmed.StartsWith('{'))
        {
            if (TryDeserializeGeometry(trimmed, out geometry, out error))
            {
                return true;
            }

            if (trimmed.Contains('\'') && !trimmed.Contains('"'))
            {
                var normalized = trimmed.Replace('\'', '"');
                if (TryDeserializeGeometry(normalized, out geometry, out error))
                {
                    return true;
                }
            }

            error = "Invalid geometry JSON.";
            return false;
        }

        Span<double> coordinates = stackalloc double[4];
        if (!TryParseCoordinateList(trimmed.AsSpan(), coordinates, out var coordinateCount, out error))
        {
            return false;
        }

        var normalizedType = geometryType?.Trim().ToLowerInvariant();
        if (normalizedType == "esrigeometryenvelope" || coordinateCount == 4)
        {
            geometry = new GeoServicesGeometry
            {
                Xmin = coordinates[0],
                Ymin = coordinates[1],
                Xmax = coordinates[2],
                Ymax = coordinates[3]
            };
            return true;
        }

        if (normalizedType == "esrigeometrypoint" || coordinateCount == 2)
        {
            geometry = new GeoServicesGeometry
            {
                X = coordinates[0],
                Y = coordinates[1]
            };
            return true;
        }

        error = "Geometry coordinate list must contain 2 values (point) or 4 values (envelope).";
        return false;
    }

    private static bool TryDeserializeGeometry(string json, out GeoServicesGeometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        try
        {
            geometry = JsonSerializer.Deserialize(json, FeatureServerJsonContext.Default.GeoServicesGeometry);
            if (geometry == null)
            {
                error = "Geometry JSON could not be parsed.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "Invalid geometry JSON.";
            return false;
        }
    }

    private static bool TryParseCoordinateList(ReadOnlySpan<char> value, Span<double> coordinates, out int coordinateCount, out string? error)
    {
        if (!value.TryParseDoubles(coordinates, _coordinateSeparators, out coordinateCount, out error))
        {
            if (coordinateCount == 0 && error == "Value list is empty.")
            {
                error = "Geometry coordinate list is empty.";
            }

            return false;
        }

        return true;
    }

}

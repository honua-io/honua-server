// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Parsing;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Models;

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
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.QueryRequested(_logger, serviceId, layerId, queryParams.Where);

            var resourceResult = await _resourceValidator.ValidateServiceLayerAsync(serviceId, layerId, cancellationToken);
            if (!resourceResult.IsValid)
            {
                var errorMessage = resourceResult.ErrorMessage ?? "Resource not found.";

                if (resourceResult.ErrorCode == ResourceValidationError.InvalidIdentifier)
                {
                    return StandardErrorHelpers.CreateBadRequest(context, errorMessage);
                }

                if (errorMessage.StartsWith("Service", StringComparison.OrdinalIgnoreCase))
                {
                    FeatureServerLog.ServiceNotFound(_logger, serviceId);
                }
                else if (errorMessage.StartsWith("Layer", StringComparison.OrdinalIgnoreCase))
                {
                    FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                }

                return StandardErrorHelpers.CreateNotFound(context, errorMessage);
            }

            ServiceDefinition service = resourceResult.Resource!.Service;
            LayerDefinition layer = resourceResult.Resource.Layer;
            var activity = Activity.Current;
            activity?.SetTag("honua.protocol", "featureserver");
            activity?.SetTag("honua.service_id", serviceId);
            activity?.SetTag("honua.layer_id", layerId.ToString(CultureInfo.InvariantCulture));
            var accessError = AccessPolicyHelpers.RequireLayerAccess(context, layer, service);
            if (accessError != null)
            {
                return accessError;
            }

            var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
            var whereValidation = queryValidator.ValidateWhereClause(queryParams.Where);
            if (!whereValidation.IsValid)
            {
                var message = whereValidation.ErrorMessage ?? ErrorMessages.Validation.InvalidParameter;
                return StandardErrorHelpers.CreateBadRequest(context,
                    ErrorMessages.Validation.InvalidParameter,
                    [message]);
            }

            // Apply limits enforcement
            QueryValidationResult validationResult = _queryServices.ValidateQueryLimits(queryParams);
            if (!validationResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "Query parameters exceed configured limits",
                    [validationResult.ErrorMessage!]);
            }

            QueryParameters validatedParams = validationResult.ValidatedParameters!;

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
                return cached == null ? null : Results.Bytes(cached.Payload, cached.ContentType);
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
                await _responseCache.SetAsync(cacheKey, new CachedResponse(payload, contentType), cacheTtl, cancellationToken);
                return Results.Bytes(payload, contentType);
            }

            GeoServicesGeometry? parsedGeometry = null;
            if (!TryParseGeoServicesGeometry(validatedParams.Geometry, validatedParams.GeometryType, out parsedGeometry, out var geometryError))
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    "ErrorMessages.Validation.InvalidGeometryParameter",
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
            FeatureQuery query = BuildFeatureQuery(validatedParams, service, layer, parsedGeometry, inputSrid, outputSrid, sqlFilter);

            var objectIdFieldName = layer.PrimaryKeyField?.Name ?? "objectid";

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
                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    Count = count
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
                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    Extent = extent.HasValue ? MapExtent(extent.Value) : null
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            if (validatedParams.ReturnIdsOnly)
            {
                var idsEffectiveLimit = query.Limit ?? service.MaxRecordCount;
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
                var hasMoreResults = query.Limit.HasValue && result.TotalCount > query.Limit.Value;

                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    ObjectIds = objectIds,
                    ExceededTransferLimit = hasMoreResults
                };

                return await CreateCachedResultAsync(response, FeatureServerJsonContext.Default.QueryResponse, "application/json");
            }

            var effectiveLimit = query.Limit ?? service.MaxRecordCount;
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

            return _streamingResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

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

            if (context.Response.HasStarted)
            {
                return _streamingResult;
            }

            return StandardErrorHelpers.CreateInternalServerError(context, "Query execution failed");
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
        ServiceDefinition service,
        LayerDefinition layer,
        GeoServicesGeometry? parsedGeometry,
        int? inputSrid,
        int? outputSrid,
        SqlFragment? sqlFilter)
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
            Limit = queryParams.ResultRecordCount ?? service.MaxRecordCount,
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

                SpatialFilter spatialFilter = ParseSpatialFilter(queryParams, parsedGeometry!, inputSrid);
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

    private static ExtentInfo MapExtent(FeatureExtent extent)
    {
        return new ExtentInfo
        {
            Xmin = extent.MinX,
            Ymin = extent.MinY,
            Xmax = extent.MaxX,
            Ymax = extent.MaxY,
            SpatialReference = new SpatialReferenceInfo { Wkid = extent.SpatialReference }
        };
    }

    /// <summary>
    /// Parses GeoServices JSON geometry and spatial relationship into a SpatialFilter
    /// </summary>
    private SpatialFilter ParseSpatialFilter(QueryParameters queryParams, GeoServicesGeometry geometry, int? inputSrid)
    {
        // Convert GeoServices JSON geometry to WKB bytes
        byte[] wkbBytes = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(geometry, inputSrid);

        // Check if this is a KNN query (NearestCount specified)
        if (queryParams.NearestCount.HasValue && queryParams.NearestCount.Value > 0)
        {
            return SpatialFilter.CreateKnnFilter(
                wkbBytes,
                queryParams.NearestCount.Value,
                queryParams.ReturnDistance,
                inputSrid);
        }

        // Map GeoServices spatial relationship to enum
        SpatialRelationship relationship = ParseSpatialRelationship(queryParams.SpatialRel);

        // Handle distance-based queries
        if (relationship == SpatialRelationship.WithinDistance ||
            relationship == SpatialRelationship.BeyondDistance)
        {
            if (!queryParams.Distance.HasValue || queryParams.Distance.Value <= 0)
            {
                throw new ArgumentException("Distance parameter is required for distance-based spatial queries");
            }

            var unit = ParseDistanceUnit(queryParams.Units);
            return SpatialFilter.CreateDistanceFilter(
                wkbBytes,
                queryParams.Distance.Value,
                unit,
                relationship == SpatialRelationship.WithinDistance,
                inputSrid);
        }

        return new SpatialFilter
        {
            Geometry = wkbBytes,
            SpatialRelationship = relationship,
            Srid = inputSrid
        };
    }

    /// <summary>
    /// Maps GeoServices spatial relationship strings to SpatialRelationship enum
    /// </summary>
    private static SpatialRelationship ParseSpatialRelationship(string? spatialRel)
    {
        return spatialRel?.ToLowerInvariant() switch
        {
            "esrispatialrelintersects" or null => SpatialRelationship.Intersects,
            "esrispatialrelcontains" => SpatialRelationship.Contains,
            "esrispatialrelwithin" => SpatialRelationship.Within,
            "esrispatialrelenvelopeintersects" => SpatialRelationship.EnvelopeIntersects,
            "esrispatialrelcrosses" => SpatialRelationship.Crosses,
            "esrispatialreltouches" => SpatialRelationship.Touches,
            "esrispatialreloverlaps" => SpatialRelationship.Overlaps,
            "esrispatialreldisjoint" => SpatialRelationship.Disjoint,
            "esrispatialrelequals" => SpatialRelationship.Equals,
            "esrispatialrelwithindistance" => SpatialRelationship.WithinDistance,
            "esrispatialrelbeyonddistance" => SpatialRelationship.BeyondDistance,
            _ => throw new ArgumentException($"Unsupported spatial relationship: {spatialRel}")
        };
    }

    /// <summary>
    /// Maps GeoServices distance unit strings to DistanceUnit enum
    /// </summary>
    private static DistanceUnit ParseDistanceUnit(string? units)
    {
        return units?.ToLowerInvariant() switch
        {
            "esrisrunit_meter" or null => DistanceUnit.Meters,
            "esrisrunit_foot" => DistanceUnit.Feet,
            "esrisrunit_kilometer" => DistanceUnit.Kilometers,
            "esrisrunit_statutemile" => DistanceUnit.Miles,
            // Also support simple unit names
            "meters" or "m" => DistanceUnit.Meters,
            "feet" or "ft" => DistanceUnit.Feet,
            "kilometers" or "km" => DistanceUnit.Kilometers,
            "miles" or "mi" => DistanceUnit.Miles,
            _ => DistanceUnit.Meters // Default to meters for unknown units
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

    private sealed record TemporalFieldSelection(FieldDefinition StartField, FieldDefinition? EndField);

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

        var selection = ResolveTemporalFields(layer);
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

    private static TemporalFieldSelection ResolveTemporalFields(LayerDefinition layer)
    {
        var timeInfo = layer.Metadata?.TimeInfo;
        FieldDefinition? startField = null;
        FieldDefinition? endField = null;

        if (!string.IsNullOrWhiteSpace(timeInfo?.StartTimeField))
        {
            startField = FindTemporalField(layer, timeInfo.StartTimeField);
            if (startField == null)
            {
                throw new ArgumentException($"Temporal field '{timeInfo.StartTimeField}' is not defined on layer '{layer.Name}'.");
            }
        }

        if (!string.IsNullOrWhiteSpace(timeInfo?.EndTimeField))
        {
            endField = FindTemporalField(layer, timeInfo.EndTimeField);
            if (endField == null)
            {
                throw new ArgumentException($"Temporal field '{timeInfo.EndTimeField}' is not defined on layer '{layer.Name}'.");
            }
        }

        if (startField == null)
        {
            startField = layer.AttributeFields.FirstOrDefault(field => field.Type is FieldType.DateTime or FieldType.Date)
                ?? throw new ArgumentException($"No temporal field found in layer '{layer.Name}' for temporal query.");
        }

        if (endField != null && endField.Type != startField.Type)
        {
            throw new ArgumentException("Start and end time fields must use the same temporal type.");
        }

        return new TemporalFieldSelection(startField, endField);
    }

    private static FieldDefinition? FindTemporalField(LayerDefinition layer, string fieldName)
    {
        return layer.AttributeFields.FirstOrDefault(field =>
            field.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase) &&
            field.Type is FieldType.DateTime or FieldType.Date);
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

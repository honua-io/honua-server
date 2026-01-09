// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
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
    private static readonly HashSet<string> _allowedCoreOrderByFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "objectid",
        "object_id",
        "created_at",
        "updated_at"
    };
    private static readonly IResult _streamingResult = new StreamingResult();
    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureServerQueryServices _queryServices = dependencies.QueryServices;
    private readonly IFilterExpressionService _filterExpressionService = dependencies.FilterExpressionService;
    private readonly FeatureServerQueryExecutor _queryExecutor = dependencies.QueryExecutor;
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

            var format = validatedParams.F ?? "json";
            if (string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Output format 'pbf' is not supported");
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

            SqlFragment? sqlFilter = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Where))
            {
                var parseResult = _filterExpressionService.Parse(FilterLanguage.Cql2Text, validatedParams.Where);
                if (parseResult.IsSuccess && parseResult.Expression != null)
                {
                    var translationResult = _filterExpressionService.Translate(parseResult.Expression, layer);
                    if (!translationResult.IsSuccess)
                    {
                        return StandardErrorHelpers.CreateBadRequest(context,
                            ErrorMessages.Validation.InvalidParameter,
                            [translationResult.ErrorMessage ?? "Invalid filter syntax."]);
                    }

                    sqlFilter = translationResult.SqlFilter;
                }
            }

            // Build query from validated parameters
            FeatureQuery query = BuildFeatureQuery(validatedParams, service, layer, parsedGeometry, inputSrid, outputSrid, sqlFilter);

            var objectIdFieldName = layer.PrimaryKeyField?.Name ?? "objectid";

            if (validatedParams.ReturnCountOnly)
            {
                var stopwatch = Stopwatch.StartNew();
                var count = await _queryExecutor.CountAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "count", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    Count = count
                };

                return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
            }

            if (validatedParams.ReturnExtentOnly)
            {
                var stopwatch = Stopwatch.StartNew();
                var extent = await _queryExecutor.GetExtentAsync(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "extent", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    Extent = extent.HasValue ? MapExtent(extent.Value) : null
                };

                return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
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

                return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
            }

            var effectiveLimit = query.Limit ?? service.MaxRecordCount;
            var useStreaming = effectiveLimit > StreamingThreshold;

            if (!useStreaming)
            {
                var queryStopwatch = Stopwatch.StartNew();
                QueryResult<Feature> result = await _queryExecutor.QueryWithValidationAsync(layerId, query, cancellationToken);
                queryStopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "query", serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

                string[]? outFields = string.IsNullOrEmpty(validatedParams.OutFields) ? null :
                    [.. validatedParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

                (object? formattedResponse, string? contentType) = _queryServices.FormatQueryResult(
                    result,
                    layer,
                    validatedParams.F ?? "json",
                    validatedParams.ReturnGeometry,
                    outputSrid,
                    outFields);

                FeatureServerLog.QueryCompleted(_logger, serviceId, layerId, result.Items.Length, result.TotalCount);

                return format.ToLowerInvariant() switch
                {
                    "geojson" => Results.Json(formattedResponse, FeatureServerJsonContext.Default.GeoJsonFeatureSet, contentType: contentType),
                    _ => Results.Json(formattedResponse, FeatureServerJsonContext.Default.QueryResponse, contentType: contentType)
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
            OrderBy = ParseOrderByFields(queryParams.OrderByFields, layer)
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

        // Parse temporal filter if specified
        if (!string.IsNullOrWhiteSpace(queryParams.Time))
        {
            try
            {
                TemporalFilter? temporalFilter = ParseTemporalFilter(queryParams, layer);
                if (temporalFilter.HasValue)
                {
                    query = query with { TemporalFilter = temporalFilter.Value };
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"Invalid temporal parameters: {ex.Message}");
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"Invalid time parameter: {ex.Message}");
            }
        }

        return query;
    }

    private static ImmutableArray<OrderByClause>? ParseOrderByFields(string? orderByFields, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(orderByFields))
        {
            return null;
        }

        var clauses = new List<OrderByClause>();
        foreach (var rawField in orderByFields.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawField.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var field = parts[0];
            if (!IsValidOrderByField(field))
            {
                throw new InvalidOperationException($"Invalid orderByFields value: {field}");
            }

            if (parts.Length > 2)
            {
                throw new InvalidOperationException($"Invalid orderByFields value: {trimmed}");
            }

            var ascending = true;
            if (parts.Length == 2)
            {
                if (parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase))
                {
                    ascending = false;
                }
                else if (!parts[1].Equals("ASC", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Invalid orderByFields direction: {parts[1]}");
                }
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (fieldDefinition == null && !_allowedCoreOrderByFields.Contains(field))
            {
                throw new InvalidOperationException($"Unknown orderByFields value: {field}");
            }

            var resolvedField = fieldDefinition?.Name ?? field;
            if (!IsValidOrderByField(resolvedField))
            {
                throw new InvalidOperationException($"Invalid orderByFields value: {field}");
            }
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
    }

    private static bool IsValidOrderByField(string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            if (!(char.IsLetterOrDigit(ch) || ch == '_'))
            {
                return false;
            }
        }

        return true;
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

    /// <summary>
    /// Parses temporal parameters and creates a TemporalFilter
    /// </summary>
    private static TemporalFilter? ParseTemporalFilter(QueryParameters queryParams, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(queryParams.Time))
        {
            return null;
        }

        // Find a temporal field in the layer (first datetime field)
        var temporalField = layer.Fields.FirstOrDefault(f =>
            f.Type == FieldType.DateTime || f.Type == FieldType.Date)
            ?? throw new InvalidOperationException($"No temporal field found in layer '{layer.Name}' for temporal query");

        var temporalPropertyType = temporalField.Type == FieldType.Date
            ? TemporalPropertyType.Date
            : TemporalPropertyType.DateTime;

        if (!TryParseTimeParameter(queryParams.Time, out var startTime, out var endTime))
        {
            throw new InvalidOperationException($"Invalid time parameter format: {queryParams.Time}");
        }

        return new TemporalFilter
        {
            PropertyName = temporalField.Name,
            PropertyType = temporalPropertyType,
            Start = startTime,
            End = endTime
        };
    }

    /// <summary>
    /// Parses time parameter string into start/end times
    /// Supports Unix timestamps in milliseconds and ISO 8601 format
    /// </summary>
    private static bool TryParseTimeParameter(string timeParam, out DateTimeOffset? start, out DateTimeOffset? end)
    {
        start = null;
        end = null;

        if (string.IsNullOrWhiteSpace(timeParam))
        {
            return false;
        }

        // Handle time extent (comma-separated values)
        if (timeParam.Contains(','))
        {
            var parts = timeParam.Split(',', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            if (!TryParseSingleTime(parts[0].Trim(), out start))
            {
                return false;
            }

            if (!TryParseSingleTime(parts[1].Trim(), out end))
            {
                return false;
            }

            return start.HasValue && end.HasValue && start.Value <= end.Value;
        }

        // Single time instant
        if (!TryParseSingleTime(timeParam, out start))
        {
            return false;
        }

        end = start; // For single time instant, start and end are the same
        return true;
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

        // Try parsing as Unix timestamp in milliseconds
        if (long.TryParse(timeValue, out var unixMs))
        {
            try
            {
                time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                return true;
            }
            catch
            {
                // Invalid Unix timestamp
            }
        }

        // Try parsing as ISO 8601
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

        if (!TryParseCoordinateList(trimmed, out var coordinates, out error))
        {
            return false;
        }

        var normalizedType = geometryType?.Trim().ToLowerInvariant();
        if (normalizedType == "esrigeometryenvelope" || coordinates.Length == 4)
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

        if (normalizedType == "esrigeometrypoint" || coordinates.Length == 2)
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

    private static bool TryParseCoordinateList(string value, out double[] coordinates, out string? error)
    {
        error = null;
        coordinates = Array.Empty<double>();

        var parts = value.Split(_coordinateSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Geometry coordinate list is empty.";
            return false;
        }

        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                error = $"Invalid coordinate value: {parts[i]}";
                return false;
            }

            values[i] = parsed;
        }

        coordinates = values;
        return true;
    }

}

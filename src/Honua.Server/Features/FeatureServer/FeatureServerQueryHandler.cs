// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer query operations.
/// </summary>
internal sealed class FeatureServerQueryHandler(
    ILayerCatalog layerCatalog,
    IFeatureStore featureStore,
    IFeatureServerQueryServices queryServices,
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
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly IFeatureStore _featureStore = featureStore ?? throw new ArgumentNullException(nameof(featureStore));
    private readonly IFeatureServerQueryServices _queryServices = queryServices ?? throw new ArgumentNullException(nameof(queryServices));
    private readonly ILogger<FeatureServerQueryHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Executes a feature query operation with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryFeaturesAsync(
        string serviceId,
        int layerId,
        QueryParameters queryParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.QueryRequested(_logger, serviceId, layerId, queryParams.Where);

            // Validate service and layer existence
            ServiceDefinition? service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            LayerDefinition? layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            var basicValidation = FeatureQueryValidationService.ValidateBasicParameters(queryParams);
            if (!basicValidation.IsValid)
            {
                var message = basicValidation.ErrorMessage ?? "Invalid query parameters";
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    [message]);
            }

            // Apply limits enforcement
            QueryValidationResult validationResult = _queryServices.ValidateQueryLimits(queryParams);
            if (!validationResult.IsValid)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Query parameters exceed configured limits",
                    [validationResult.ErrorMessage!]);
            }

            QueryParameters validatedParams = validationResult.ValidatedParameters!;

            var format = validatedParams.F ?? "json";
            if (string.Equals(format, "pbf", StringComparison.OrdinalIgnoreCase))
            {
                return GeoServicesErrorHelpers.CreateBadRequestError("Output format 'pbf' is not supported");
            }

            GeoServicesGeometry? parsedGeometry = null;
            if (!TryParseGeoServicesGeometry(validatedParams.Geometry, validatedParams.GeometryType, out parsedGeometry, out var geometryError))
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid geometry parameter",
                    [geometryError ?? "Geometry parameter is invalid."]);
            }

            var inputSrid = await _queryServices.ResolveSridAsync(validatedParams.InSr, parsedGeometry?.SpatialReference, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.InSr) && !inputSrid.HasValue)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid input spatial reference",
                    [$"Unsupported inSR value: {validatedParams.InSr}"]);
            }

            if (parsedGeometry != null && !inputSrid.HasValue)
            {
                inputSrid = layer.SpatialReference.Srid;
            }

            var outputSrid = await _queryServices.ResolveSridAsync(validatedParams.OutSr, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.OutSr) && !outputSrid.HasValue)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid output spatial reference",
                    [$"Unsupported outSR value: {validatedParams.OutSr}"]);
            }

            SqlFragment? sqlFilter = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Where))
            {
                FilterExpression? filterExpression = null;
                try
                {
                    var parser = new Cql2Parser();
                    filterExpression = parser.Parse(validatedParams.Where);
                }
                catch (ArgumentException)
                {
                    // Fall back to legacy WHERE parsing for backwards compatibility.
                }

                if (filterExpression != null)
                {
                    try
                    {
                        sqlFilter = _queryServices.TranslateFilter(filterExpression, layer);
                    }
                    catch (ArgumentException)
                    {
                        return GeoServicesErrorHelpers.CreateBadRequestError(
                            "Invalid query parameters",
                            ["Invalid filter syntax."]);
                    }
                    catch (NotSupportedException)
                    {
                        return GeoServicesErrorHelpers.CreateBadRequestError(
                            "Invalid query parameters",
                            ["Unsupported filter syntax."]);
                    }
                }
            }

            // Build query from validated parameters
            FeatureQuery query = BuildFeatureQuery(validatedParams, service, layer, parsedGeometry, inputSrid, outputSrid, sqlFilter);

            var objectIdFieldName = layer.PrimaryKeyField?.Name ?? "objectid";

            if (validatedParams.ReturnCountOnly)
            {
                var stopwatch = Stopwatch.StartNew();
                var count = await _featureStore.CountAsync(layerId, query, cancellationToken);
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
                var extent = await _featureStore.GetExtentAsync(layerId, query, cancellationToken);
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
                var stopwatch = Stopwatch.StartNew();
                QueryResult<Feature> idResult = await ExecuteQueryWithValidation(layerId, query, cancellationToken);
                stopwatch.Stop();
                FeatureServerLog.QueryExecuted(_logger, "ids", serviceId, layerId, stopwatch.Elapsed.TotalMilliseconds);
                var response = new QueryResponse
                {
                    ObjectIdFieldName = objectIdFieldName,
                    ObjectIds = idResult.Items.Select(feature => feature.Id).ToArray(),
                    ExceededTransferLimit = idResult.HasMoreResults
                };

                return Results.Json(response, FeatureServerJsonContext.Default.QueryResponse, contentType: "application/json");
            }

            // Execute query
            var queryStopwatch = Stopwatch.StartNew();
            QueryResult<Feature> result = await ExecuteQueryWithValidation(layerId, query, cancellationToken);
            queryStopwatch.Stop();
            FeatureServerLog.QueryExecuted(_logger, "query", serviceId, layerId, queryStopwatch.Elapsed.TotalMilliseconds);

            // Format response using QueryFormatter
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

            // Return response with appropriate content type and JSON context
            return format.ToLowerInvariant() switch
            {
                "geojson" => Results.Json(formattedResponse, FeatureServerJsonContext.Default.GeoJsonFeatureSet, contentType: contentType),
                _ => Results.Json(formattedResponse, FeatureServerJsonContext.Default.QueryResponse, contentType: contentType)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            // Return safe error message without leaking exception details
            return GeoServicesErrorHelpers.CreateBadRequestError("Invalid query parameters");
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return GeoServicesErrorHelpers.CreateInternalServerError("Query execution failed");
        }
    }

    /// <summary>
    /// Executes a query for related records with proper validation and formatting.
    /// </summary>
    public async Task<IResult> HandleQueryRelatedRecordsAsync(
        string serviceId,
        int layerId,
        QueryRelatedRecordsParameters queryParams,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string objectIdsString = string.Join(",", queryParams.ObjectIds);
            FeatureServerLog.RelatedRecordsQueryRequested(_logger, serviceId, layerId, objectIdsString, queryParams.RelationshipId);

            // Validate service and layer existence
            ServiceDefinition? service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            if (service == null)
            {
                FeatureServerLog.ServiceNotFound(_logger, serviceId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
            }

            LayerDefinition? layer = service.Layers.FirstOrDefault(l => l.Id == layerId);
            if (layer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
                return GeoServicesErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
            }

            // Validate required parameters (these should already be validated by parameter parsing)
            if (queryParams.ObjectIds.Length == 0)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    ["objectIds parameter is required"]);
            }

            // Validate relationship exists
            var relationshipMaybe = await _layerCatalog.GetRelationshipAsync(layerId, queryParams.RelationshipId, cancellationToken);
            if (relationshipMaybe == null)
            {
                FeatureServerLog.RelationshipNotFound(_logger, layerId, queryParams.RelationshipId);
                return GeoServicesErrorHelpers.CreateNotFoundError(
                    $"Relationship {queryParams.RelationshipId} not found for layer {layerId}");
            }

            var relationship = relationshipMaybe.Value;

            // Apply limits enforcement
            RelatedRecordsValidationResult validationResult = _queryServices.ValidateRelatedRecordsLimits(queryParams);
            if (!validationResult.IsValid)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Query parameters exceed configured limits",
                    [validationResult.ErrorMessage!]);
            }

            QueryRelatedRecordsParameters validatedParams = validationResult.ValidatedParameters!;

            // Get related layer information
            var relatedLayer = service.Layers.FirstOrDefault(l => l.Id == relationship!.RelatedLayerId);
            if (relatedLayer == null)
            {
                FeatureServerLog.LayerNotFound(_logger, serviceId, relationship.RelatedLayerId);
                return GeoServicesErrorHelpers.CreateNotFoundError(
                    $"Related layer {relationship.RelatedLayerId} not found in service '{serviceId}'");
            }

            var objectIds = queryParams.ObjectIds;

            // Build related query from validated parameters
            RelatedQuery relatedQuery = BuildRelatedQuery(validatedParams, objectIds, (Relationship)relationship);

            // Execute related query
            QueryResult<Feature> result = await ExecuteRelatedQueryWithValidation(layerId, relatedQuery, cancellationToken);

            // Group results by origin object ID
            RelatedRecordGroup[] relatedRecordGroups = GroupRelatedRecords(
                result,
                objectIds,
                (Relationship)relationship,
                validatedParams.ReturnGeometry,
                relatedLayer.SpatialReference.Srid);

            // Build response
            var response = new QueryRelatedRecordsResponse
            {
                RelatedRecordGroups = relatedRecordGroups
            };

            FeatureServerLog.RelatedRecordsQueryCompleted(_logger, serviceId, layerId,
                relatedRecordGroups.Sum(g => g.RelatedRecords?.Features?.Length ?? 0), relatedRecordGroups.Length);

            return Results.Json(response, FeatureServerJsonContext.Default.QueryRelatedRecordsResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            // Return safe error message without leaking exception details
            return GeoServicesErrorHelpers.CreateBadRequestError("Invalid query parameters");
        }
        catch (Exception ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return GeoServicesErrorHelpers.CreateInternalServerError("Related records query execution failed");
        }
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
            SpatialReferenceSrid = layer.SpatialReference.Srid,
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
            try
            {
                geometry = JsonSerializer.Deserialize(trimmed, FeatureServerJsonContext.Default.GeoServicesGeometry);
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

    /// <summary>
    /// Executes a query with validation error handling
    /// </summary>
    private async Task<QueryResult<Feature>> ExecuteQueryWithValidation(int layerId, FeatureQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await _featureStore.QueryAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid query format: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("syntax") || ex.Message.Contains("SQL") || ex.Message.Contains("parse"))
        {
            throw new InvalidOperationException($"Invalid query syntax: {ex.Message}");
        }
    }



    /// <summary>
    /// Builds a FeatureQuery for related records from query parameters
    /// </summary>
    private static RelatedQuery BuildRelatedQuery(QueryRelatedRecordsParameters queryParams, long[] objectIds, Relationship relationship)
    {
        var query = new RelatedQuery
        {
            ObjectIds = objectIds,
            Relationship = relationship,
            Where = queryParams.Where,
            Limit = queryParams.ResultRecordCount
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

        return query;
    }

    /// <summary>
    /// Executes a related records query with validation error handling
    /// </summary>
    private async Task<QueryResult<Feature>> ExecuteRelatedQueryWithValidation(int layerId, RelatedQuery query, CancellationToken cancellationToken)
    {
        try
        {
            return await _featureStore.QueryRelatedAsync(layerId, query, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Invalid related query: {ex.Message}");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Invalid related query format: {ex.Message}");
        }
        catch (Exception ex) when (ex.Message.Contains("syntax") || ex.Message.Contains("SQL") || ex.Message.Contains("parse"))
        {
            throw new InvalidOperationException($"Invalid related query syntax: {ex.Message}");
        }
    }

    /// <summary>
    /// Groups related records by their origin object IDs
    /// </summary>
    private static RelatedRecordGroup[] GroupRelatedRecords(
        QueryResult<Feature> result,
        long[] objectIds,
        Relationship relationship,
        bool returnGeometry,
        int? outputSrid)
    {
        var featuresByOriginId = new Dictionary<long, List<Feature>>();

        foreach (var feature in result.Items)
        {
            if (feature.Attributes?.TryGetValue(relationship.DestinationForeignKeyField, out object? fkValue) == true &&
                FeatureServerValueParser.TryConvertToLong(fkValue, out var originId))
            {
                if (!featuresByOriginId.TryGetValue(originId, out var bucket))
                {
                    bucket = [];
                    featuresByOriginId[originId] = bucket;
                }

                bucket.Add(feature);
            }
        }

        // Create a related record group for each requested object ID
        return [.. objectIds.Select(objectId =>
        {
            bool hasRelatedFeatures = featuresByOriginId.TryGetValue(objectId, out List<Feature>? relatedFeatures);
            var spatialReference = outputSrid.HasValue && outputSrid.Value > 0
                ? new GeoServicesSpatialReference { Wkid = outputSrid.Value, LatestWkid = outputSrid.Value }
                : null;

            return new RelatedRecordGroup
            {
                ObjectId = objectId,
                RelatedRecords = hasRelatedFeatures && relatedFeatures!.Count > 0
                    ? new RelatedRecords
                    {
                        SpatialReference = spatialReference,
                        Features = [.. relatedFeatures!.Select(f => ConvertToGeoServicesFeature(f, returnGeometry, outputSrid))]
                    }
                    : null
            };
        })];
    }

    /// <summary>
    /// Converts a Feature to GeoServicesFeature for API responses
    /// </summary>
    private static GeoServicesFeature ConvertToGeoServicesFeature(Feature feature, bool returnGeometry, int? outputSrid)
    {
        var attributes = feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        return new GeoServicesFeature
        {
            Attributes = attributes,
            Geometry = returnGeometry ? GeoServicesGeometryConverter.ConvertWkbToGeoServicesGeometry(feature.Geometry, outputSrid) : null
        };
    }

}

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
/// Handler for FeatureServer operations that consolidates dependencies to reduce DI coupling.
/// Addresses architectural limit of 5 dependencies per endpoint / 4 per handler.
/// </summary>
/// <remarks>
/// Initializes a new FeatureServerHandler with required dependencies.
/// Uses FeatureServerServices to aggregate query-related services, reducing dependency count to 4.
/// </remarks>
internal sealed class FeatureServerHandler(
    ILayerCatalog layerCatalog,
    IFeatureStore featureStore,
    FeatureServerServices services,
    ILogger<FeatureServerHandler> logger)
{
    private static readonly char[] _coordinateSeparators = { ',', ' ' };
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly IFeatureStore _featureStore = featureStore ?? throw new ArgumentNullException(nameof(featureStore));
    private readonly FeatureServerServices _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly ILogger<FeatureServerHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

            if (queryParams.ResultRecordCount is < 1)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    [$"{nameof(QueryParameters.ResultRecordCount)} must be greater than 0"]);
            }

            if (queryParams.ResultOffset is < 0)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid query parameters",
                    [$"{nameof(QueryParameters.ResultOffset)} must be 0 or greater"]);
            }

            // Apply limits enforcement
            QueryValidationResult validationResult = _services.QueryValidator.ValidateQueryLimits(queryParams);
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

            var inputSrid = await ResolveSridAsync(validatedParams.InSr, parsedGeometry?.SpatialReference, cancellationToken);
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

            var outputSrid = await ResolveSridAsync(validatedParams.OutSr, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(validatedParams.OutSr) && !outputSrid.HasValue)
            {
                return GeoServicesErrorHelpers.CreateBadRequestError(
                    "Invalid output spatial reference",
                    [$"Unsupported outSR value: {validatedParams.OutSr}"]);
            }

            SqlFragment? sqlFilter = null;
            if (!string.IsNullOrWhiteSpace(validatedParams.Where))
            {
                try
                {
                    var parser = new Cql2Parser();
                    var filterExpression = parser.Parse(validatedParams.Where);
                    sqlFilter = _services.SqlFilterTranslator.Translate(filterExpression, layer);
                }
                catch (ArgumentException ex)
                {
                    return GeoServicesErrorHelpers.CreateBadRequestError(
                        "Invalid query parameters",
                        [ex.Message]);
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

            (object? formattedResponse, string? contentType) = _services.QueryFormatter.FormatQueryResult(
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

            return GeoServicesErrorHelpers.CreateBadRequestError(
                "Invalid query parameters",
                [ex.Message]);
        }
        catch (Exception ex)
        {
            FeatureServerLog.QueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return GeoServicesErrorHelpers.CreateInternalServerError(
                "Query execution failed");
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
            RelatedRecordsValidationResult validationResult = _services.QueryValidator.ValidateRelatedRecordsLimits(queryParams);
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

            return GeoServicesErrorHelpers.CreateBadRequestError(
                "Invalid query parameters",
                [ex.Message]);
        }
        catch (Exception ex)
        {
            FeatureServerLog.RelatedRecordsQueryFailed(_logger, serviceId, layerId, ex.Message, ex);

            return GeoServicesErrorHelpers.CreateInternalServerError(
                "Related records query execution failed");
        }
    }

    /// <summary>
    /// Handles applyEdits requests for adding, updating, and deleting features.
    /// </summary>
    public async Task<IResult> HandleApplyEditsAsync(
        string serviceId,
        int layerId,
        ApplyEditsRequest request,
        Honua.Core.Configuration.EditLimits editLimits,
        CancellationToken cancellationToken = default)
    {
        try
        {
            FeatureServerLog.ApplyEditsRequested(_logger, serviceId, layerId,
                request.Adds?.Length ?? 0,
                request.Updates?.Length ?? 0,
                request.Deletes?.Length ?? 0);

            // Validate service and layer exist
            var validationResult = await ValidateServiceAndLayer(serviceId, layerId, cancellationToken);
            if (validationResult != null)
            {
                return validationResult;
            }

            var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
            var layer = service!.GetLayer(layerId)!;

            // Validate edit limits
            var limitsValidationResult = ValidateEditLimits(request, editLimits);
            if (limitsValidationResult != null)
            {
                return limitsValidationResult;
            }

            var totalCount = (request.Adds?.Length ?? 0) + (request.Updates?.Length ?? 0) + (request.Deletes?.Length ?? 0);
            if (totalCount == 0)
            {
                return Results.Json(new ApplyEditsResponse { Success = true },
                    FeatureServerJsonContext.Default.ApplyEditsResponse,
                    contentType: "application/json");
            }

            // Process edit operations
            var editContext = ProcessEditOperations(request, layer.SpatialReference.Srid);

            // Handle validation errors with rollback if needed
            if (editContext.HasValidationErrors && request.RollbackOnFailure)
            {
                return CreateRollbackResponse(editContext, serviceId, layerId);
            }

            // Execute edits in the database
            var editResult = await ExecuteEdits(layer!.Id, editContext, request, cancellationToken);

            // Build and return final response
            return CreateFinalResponse(editContext, editResult, serviceId, layerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            return GeoServicesErrorHelpers.CreateInternalServerError("Apply edits failed");
        }
    }

    /// <summary>
    /// Validates that the service and layer exist
    /// </summary>
    private async Task<IResult?> ValidateServiceAndLayer(string serviceId, int layerId, CancellationToken cancellationToken)
    {
        var service = await _layerCatalog.GetServiceAsync(serviceId, cancellationToken);
        if (service == null)
        {
            FeatureServerLog.ServiceNotFound(_logger, serviceId);
            return GeoServicesErrorHelpers.CreateNotFoundError($"Service '{serviceId}' not found");
        }

        var layer = service.GetLayer(layerId);
        if (layer == null)
        {
            FeatureServerLog.LayerNotFound(_logger, serviceId, layerId);
            return GeoServicesErrorHelpers.CreateNotFoundError($"Layer {layerId} not found in service '{serviceId}'");
        }

        return null;
    }

    /// <summary>
    /// Validates edit operation counts against configured limits
    /// </summary>
    private static IResult? ValidateEditLimits(ApplyEditsRequest request, Honua.Core.Configuration.EditLimits editLimits)
    {
        var addCount = request.Adds?.Length ?? 0;
        var updateCount = request.Updates?.Length ?? 0;
        var deleteCount = request.Deletes?.Length ?? 0;
        var totalCount = addCount + updateCount + deleteCount;

        if (addCount > editLimits.MaxFeaturesPerEdit ||
            updateCount > editLimits.MaxFeaturesPerEdit ||
            deleteCount > editLimits.MaxFeaturesPerEdit)
        {
            return GeoServicesErrorHelpers.CreateBadRequestError(
                "Too many features in a single edit operation",
                [$"Maximum per operation: {editLimits.MaxFeaturesPerEdit}"]);
        }

        if (totalCount > editLimits.MaxEditsPerTransaction)
        {
            return GeoServicesErrorHelpers.CreateBadRequestError(
                "Too many edits in a single request",
                [$"Maximum per request: {editLimits.MaxEditsPerTransaction}"]);
        }

        return null;
    }

    /// <summary>
    /// Processes add, update, and delete operations from the request
    /// </summary>
    private EditOperationContext ProcessEditOperations(ApplyEditsRequest request, int? layerSrid)
    {
        var context = new EditOperationContext
        {
            AddResults = request.Adds is { Length: > 0 } ? new EditResult?[request.Adds.Length] : null,
            UpdateResults = request.Updates is { Length: > 0 } ? new EditResult?[request.Updates.Length] : null,
            DeleteResults = request.Deletes is { Length: > 0 } ? new EditResult?[request.Deletes.Length] : null
        };

        ProcessAddOperations(request, context, layerSrid);
        ProcessUpdateOperations(request, context, layerSrid);
        ProcessDeleteOperations(request, context);

        return context;
    }

    /// <summary>
    /// Processes add operations and tracks features to create
    /// </summary>
    private void ProcessAddOperations(ApplyEditsRequest request, EditOperationContext context, int? layerSrid)
    {
        if (request.Adds == null)
            return;

        for (var i = 0; i < request.Adds.Length; i++)
        {
            try
            {
                var newFeature = BuildFeatureFromGeoServices(request.Adds[i], 0, layerSrid);
                context.CreateFeatures.Add(newFeature);
                context.CreateIndexes.Add(i);
            }
            catch (Exception)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: 1000,
                    description: "Failed to add feature");
            }
        }
    }

    /// <summary>
    /// Processes update operations and tracks features to update
    /// </summary>
    private void ProcessUpdateOperations(ApplyEditsRequest request, EditOperationContext context, int? layerSrid)
    {
        if (request.Updates == null)
            return;

        for (var i = 0; i < request.Updates.Length; i++)
        {
            var update = request.Updates[i];
            if (!TryGetObjectId(update.Attributes, out var objectId))
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1001,
                    description: "ObjectId is required for update operations");
                continue;
            }

            try
            {
                var updateFeature = BuildFeatureFromGeoServices(update, objectId, layerSrid);
                context.UpdateFeatures.Add(updateFeature);
                context.UpdateIndexes.Add(i);
                context.UpdateObjectIds.Add(objectId);
            }
            catch (Exception)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1002,
                    description: "Failed to update feature",
                    objectId: objectId);
            }
        }
    }

    /// <summary>
    /// Processes delete operations and tracks features to delete
    /// </summary>
    private static void ProcessDeleteOperations(ApplyEditsRequest request, EditOperationContext context)
    {
        if (request.Deletes == null)
            return;

        for (var i = 0; i < request.Deletes.Length; i++)
        {
            if (!TryConvertToLong(request.Deletes[i], out var objectId))
            {
                context.HasValidationErrors = true;
                context.DeleteResults![i] = CreateFailureResult(
                    code: 1003,
                    description: "Invalid ObjectId for delete operation");
                continue;
            }

            context.DeleteIds.Add(objectId);
            context.DeleteIndexes.Add(i);
        }
    }

    /// <summary>
    /// Executes the validated edit operations in the database
    /// </summary>
    private async Task<FeatureEditResult> ExecuteEdits(
        int layerId,
        EditOperationContext context,
        ApplyEditsRequest request,
        CancellationToken cancellationToken)
    {
        if (context.CreateFeatures.Count == 0 && context.UpdateFeatures.Count == 0 && context.DeleteIds.Count == 0)
        {
            return FeatureEditResult.Success(0, 0, 0);
        }

        var editBatch = FeatureEditBatch.Create(
            creates: context.CreateFeatures.ToImmutableArray(),
            updates: context.UpdateFeatures.ToImmutableArray(),
            deletes: context.DeleteIds.ToImmutableArray(),
            rollbackOnFailure: request.RollbackOnFailure,
            useGlobalIds: request.UseGlobalIds);

        var editResult = await _featureStore.ApplyEditsAsync(layerId, editBatch, cancellationToken);

        ApplyResults(context.AddResults, context.CreateIndexes, editResult.CreateResults);
        ApplyResults(context.UpdateResults, context.UpdateIndexes, editResult.UpdateResults);
        ApplyResults(context.DeleteResults, context.DeleteIndexes, editResult.DeleteResults);

        return editResult;
    }

    /// <summary>
    /// Creates a rollback response when validation errors occur
    /// </summary>
    private IResult CreateRollbackResponse(EditOperationContext context, string serviceId, int layerId)
    {
        var rollbackError = new EditError
        {
            Code = 1000,
            Description = "Operation rolled back due to validation failure"
        };

        ApplyRollbackResults(context.AddResults, context.CreateIndexes, null, rollbackError);
        ApplyRollbackResults(context.UpdateResults, context.UpdateIndexes, context.UpdateObjectIds, rollbackError);
        ApplyRollbackResults(context.DeleteResults, context.DeleteIndexes, context.DeleteIds, rollbackError);

        var response = new ApplyEditsResponse
        {
            AddResults = FinalizeResults(context.AddResults),
            UpdateResults = FinalizeResults(context.UpdateResults),
            DeleteResults = FinalizeResults(context.DeleteResults),
            Success = false
        };

        FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, false);

        return Results.Json(response, FeatureServerJsonContext.Default.ApplyEditsResponse,
            contentType: "application/json");
    }

    /// <summary>
    /// Creates the final response after all operations complete
    /// </summary>
    private IResult CreateFinalResponse(EditOperationContext context, FeatureEditResult editResult, string serviceId, int layerId)
    {
        var finalAddResults = FinalizeResults(context.AddResults);
        var finalUpdateResults = FinalizeResults(context.UpdateResults);
        var finalDeleteResults = FinalizeResults(context.DeleteResults);
        var allSuccess = AreAllResultsSuccessful(finalAddResults) &&
                         AreAllResultsSuccessful(finalUpdateResults) &&
                         AreAllResultsSuccessful(finalDeleteResults) &&
                         !editResult.WasRolledBack &&
                         !context.HasValidationErrors;

        var finalResponse = new ApplyEditsResponse
        {
            AddResults = finalAddResults,
            UpdateResults = finalUpdateResults,
            DeleteResults = finalDeleteResults,
            Success = allSuccess
        };

        FeatureServerLog.ApplyEditsCompleted(_logger, serviceId, layerId, allSuccess);

        return Results.Json(finalResponse, FeatureServerJsonContext.Default.ApplyEditsResponse,
            contentType: "application/json");
    }

    /// <summary>
    /// Context object to track edit operations state
    /// </summary>
    private sealed class EditOperationContext
    {
        public EditResult?[]? AddResults { get; init; }
        public EditResult?[]? UpdateResults { get; init; }
        public EditResult?[]? DeleteResults { get; init; }
        public List<Feature> CreateFeatures { get; } = new();
        public List<int> CreateIndexes { get; } = new();
        public List<Feature> UpdateFeatures { get; } = new();
        public List<int> UpdateIndexes { get; } = new();
        public List<long> UpdateObjectIds { get; } = new();
        public List<long> DeleteIds { get; } = new();
        public List<int> DeleteIndexes { get; } = new();
        public bool HasValidationErrors { get; set; }
    }

    private Feature BuildFeatureFromGeoServices(GeoServicesFeature feature, long objectId, int? layerSrid)
    {
        byte[]? geometry = null;
        if (feature.Geometry != null)
        {
            var geometrySrid = feature.Geometry.SpatialReference?.Wkid
                ?? feature.Geometry.SpatialReference?.LatestWkid
                ?? layerSrid;
            geometry = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(feature.Geometry, geometrySrid);
        }

        var attributes = (feature.Attributes ?? new Dictionary<string, object?>()).ToImmutableDictionary();
        return Feature.Create(objectId, geometry, attributes);
    }

    private static bool TryGetObjectId(Dictionary<string, object?>? attributes, out long objectId)
    {
        objectId = 0;

        if (attributes == null || attributes.Count == 0)
        {
            return false;
        }

        foreach (var entry in attributes)
        {
            if (string.Equals(entry.Key, "objectid", StringComparison.OrdinalIgnoreCase))
            {
                return TryConvertToLong(entry.Value, out objectId);
            }
        }

        return false;
    }

    private static bool TryConvertToLong(object? value, out long result)
    {
        result = 0;

        if (value == null)
        {
            return false;
        }

        switch (value)
        {
            case long longValue:
                result = longValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case uint uintValue:
                result = uintValue;
                return true;
            case ulong ulongValue when ulongValue <= long.MaxValue:
                result = (long)ulongValue;
                return true;
            case string stringValue:
                return long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var elementLong))
                {
                    result = elementLong;
                    return true;
                }

                if (element.ValueKind == JsonValueKind.String)
                {
                    return long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
                }

                return false;
            default:
                return long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }
    }

    private static EditResult CreateFailureResult(int code, string description, long? objectId = null, string? globalId = null)
    {
        return new EditResult
        {
            ObjectId = objectId,
            GlobalId = globalId,
            Success = false,
            Error = new EditError
            {
                Code = code,
                Description = description
            }
        };
    }

    private static EditResult ConvertEditOperationResult(EditOperationResult result)
    {
        if (result.IsSuccess)
        {
            return new EditResult
            {
                ObjectId = result.ObjectId,
                GlobalId = result.GlobalId,
                Success = true
            };
        }

        return CreateFailureResult(
            result.ErrorCode,
            result.ErrorMessage ?? "Operation failed",
            result.ObjectId,
            result.GlobalId);
    }

    private static void ApplyRollbackResults(EditResult?[]? results, List<int> indexes, List<long>? objectIds, EditError rollbackError)
    {
        if (results == null)
        {
            return;
        }

        for (var i = 0; i < indexes.Count; i++)
        {
            long? objectId = null;
            if (objectIds != null && i < objectIds.Count)
            {
                objectId = objectIds[i];
            }

            results[indexes[i]] = CreateFailureResult(rollbackError.Code, rollbackError.Description, objectId);
        }
    }

    private static void ApplyResults(EditResult?[]? results, List<int> indexes, ImmutableArray<EditOperationResult> operationResults)
    {
        if (results == null)
        {
            return;
        }

        var count = Math.Min(indexes.Count, operationResults.Length);
        for (var i = 0; i < count; i++)
        {
            results[indexes[i]] = ConvertEditOperationResult(operationResults[i]);
        }

        for (var i = count; i < indexes.Count; i++)
        {
            results[indexes[i]] ??= CreateFailureResult(1000, "Operation failed");
        }
    }

    private static EditResult[]? FinalizeResults(EditResult?[]? results)
    {
        if (results == null)
        {
            return null;
        }

        var finalized = new EditResult[results.Length];
        for (var i = 0; i < results.Length; i++)
        {
            finalized[i] = results[i] ?? CreateFailureResult(1000, "Operation failed");
        }

        return finalized;
    }

    private static bool AreAllResultsSuccessful(EditResult[]? results)
    {
        if (results == null)
        {
            return true;
        }

        return results.All(result => result.Success);
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
            Where = effectiveSqlFilter == null ? effectiveWhere : null,
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
            var ascending = true;

            if (parts.Length > 1)
            {
                ascending = !parts[1].Equals("DESC", StringComparison.OrdinalIgnoreCase);
            }

            var fieldDefinition = layer.Fields.FirstOrDefault(f =>
                f.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            var resolvedField = fieldDefinition?.Name ?? field;
            var fieldType = fieldDefinition?.Type;

            clauses.Add(new OrderByClause(resolvedField, ascending, fieldType));
        }

        return clauses.Count == 0 ? null : clauses.ToImmutableArray();
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

    private async Task<int?> ResolveSridAsync(
        string? srValue,
        GeoServicesSpatialReference? geometrySpatialReference,
        CancellationToken cancellationToken)
    {
        var srid = await ParseSridAsync(srValue, cancellationToken);
        if (srid.HasValue)
        {
            return srid;
        }

        if (geometrySpatialReference != null)
        {
            if (geometrySpatialReference.Wkid > 0)
            {
                return geometrySpatialReference.Wkid;
            }

            if (geometrySpatialReference.LatestWkid.HasValue)
            {
                return geometrySpatialReference.LatestWkid.Value;
            }

            if (!string.IsNullOrWhiteSpace(geometrySpatialReference.Wkt))
            {
                return await _services.CrsDetectionService.DetectFromWktAsync(geometrySpatialReference.Wkt);
            }
        }

        return null;
    }

    private async Task<int?> ParseSridAsync(string? srValue, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(srValue))
        {
            return null;
        }

        var trimmed = srValue.Trim();

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
        {
            return srid;
        }

        if (trimmed.StartsWith('{'))
        {
            if (TryParseSpatialReferenceJson(trimmed, out var wkid, out var wkt, out var name))
            {
                if (wkid.HasValue)
                {
                    return wkid.Value;
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    var epsg = _services.CrsDetectionService.DetectFromEpsgCode(name);
                    if (epsg.HasValue)
                    {
                        return epsg.Value;
                    }
                }

                if (!string.IsNullOrWhiteSpace(wkt))
                {
                    return await _services.CrsDetectionService.DetectFromWktAsync(wkt);
                }
            }
        }

        var detected = _services.CrsDetectionService.DetectFromEpsgCode(trimmed);
        if (detected.HasValue)
        {
            return detected.Value;
        }

        if (LooksLikeWkt(trimmed))
        {
            return await _services.CrsDetectionService.DetectFromWktAsync(trimmed);
        }

        return null;
    }

    private static bool LooksLikeWkt(string value)
    {
        return value.StartsWith("GEOGCS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("PROJCS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("GEOGCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("PROJCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("GEODCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("GEODETICCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("COMPD_CS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("COMPOUNDCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("VERT_CS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("VERTCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("LOCAL_CS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("LOCALCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("BOUNDCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("ENGCRS[", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("ENGINEERINGCRS[", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseSpatialReferenceJson(string value, out int? wkid, out string? wkt, out string? name)
    {
        wkid = null;
        wkt = null;
        name = null;

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;

            if (root.TryGetProperty("wkid", out var wkidElement) && wkidElement.TryGetInt32(out var wkidValue))
            {
                wkid = wkidValue;
            }

            if (!wkid.HasValue &&
                root.TryGetProperty("latestWkid", out var latestElement) &&
                latestElement.TryGetInt32(out var latestWkid))
            {
                wkid = latestWkid;
            }

            if (root.TryGetProperty("wkt", out var wktElement))
            {
                wkt = wktElement.GetString();
            }

            if (root.TryGetProperty("name", out var nameElement))
            {
                name = nameElement.GetString();
            }

            return wkid.HasValue || !string.IsNullOrWhiteSpace(wkt) || !string.IsNullOrWhiteSpace(name);
        }
        catch (JsonException)
        {
            return false;
        }
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
            catch (JsonException ex)
            {
                error = $"Invalid geometry JSON: {ex.Message}";
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
                TryConvertToLong(fkValue, out var originId))
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

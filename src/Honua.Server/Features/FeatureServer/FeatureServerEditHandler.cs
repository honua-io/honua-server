// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer edit operations with explicit geometry validation.
/// </summary>
internal sealed class FeatureServerEditHandler(
    ILayerCatalog layerCatalog,
    IFeatureStore featureStore,
    IFeatureServerGeometryServices geometryServices,
    ILogger<FeatureServerEditHandler> logger)
{
    private readonly ILayerCatalog _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
    private readonly IFeatureStore _featureStore = featureStore ?? throw new ArgumentNullException(nameof(featureStore));
    private readonly IFeatureServerGeometryServices _geometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
    private readonly ILogger<FeatureServerEditHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
            var editContext = ProcessEditOperations(request, layer);

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
    private EditOperationContext ProcessEditOperations(ApplyEditsRequest request, LayerDefinition layer)
    {
        var context = new EditOperationContext
        {
            AddResults = request.Adds is { Length: > 0 } ? new EditResult?[request.Adds.Length] : null,
            UpdateResults = request.Updates is { Length: > 0 } ? new EditResult?[request.Updates.Length] : null,
            DeleteResults = request.Deletes is { Length: > 0 } ? new EditResult?[request.Deletes.Length] : null
        };

        ProcessAddOperations(request, context, layer);
        ProcessUpdateOperations(request, context, layer);
        ProcessDeleteOperations(request, context);

        return context;
    }

    /// <summary>
    /// Processes add operations and tracks features to create
    /// </summary>
    private void ProcessAddOperations(ApplyEditsRequest request, EditOperationContext context, LayerDefinition layer)
    {
        if (request.Adds == null)
            return;

        for (var i = 0; i < request.Adds.Length; i++)
        {
            try
            {
                var newFeature = BuildFeatureFromGeoServices(request.Adds[i], 0, layer);
                context.CreateFeatures.Add(newFeature);
                context.CreateIndexes.Add(i);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: 1000,
                    description: ex.Message);
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
    private void ProcessUpdateOperations(ApplyEditsRequest request, EditOperationContext context, LayerDefinition layer)
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
                var updateFeature = BuildFeatureFromGeoServices(update, objectId, layer);
                context.UpdateFeatures.Add(updateFeature);
                context.UpdateIndexes.Add(i);
                context.UpdateObjectIds.Add(objectId);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1002,
                    description: ex.Message,
                    objectId: objectId);
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
            if (!FeatureServerValueParser.TryConvertToLong(request.Deletes[i], out var objectId))
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

    private Feature BuildFeatureFromGeoServices(GeoServicesFeature feature, long objectId, LayerDefinition layer)
    {
        byte[]? geometry = null;
        if (feature.Geometry != null)
        {
            // Layer 1: Validate Esri JSON input
            var esriValidation = _geometryServices.ValidateEsriJson(feature.Geometry);
            if (!esriValidation.IsValid)
            {
                var errorMessages = string.Join("; ", esriValidation.Errors.Select(e => e.Message));
                throw new ArgumentException($"Geometry validation failed: {errorMessages}");
            }

            var geometrySrid = feature.Geometry.SpatialReference?.Wkid
                ?? feature.Geometry.SpatialReference?.LatestWkid
                ?? layer.SpatialReference.Srid;
            geometry = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(feature.Geometry, geometrySrid);

            // Layer 2: Validate WKB structure and size limits
            var wkbValidation = _geometryServices.ValidateWkb(geometry);
            if (!wkbValidation.IsValid)
            {
                var errorMessages = string.Join("; ", wkbValidation.Errors.Select(e => e.Message));
                throw new ArgumentException($"WKB validation failed: {errorMessages}");
            }
        }

        var attributesResult = layer.ValidateAttributes(
            feature.Attributes,
            ValidationExtensions.AttributeValidationMode.GeoServices);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(attributesResult.ErrorMessage ?? "Invalid attributes.");
        }

        return Feature.Create(objectId, geometry, attributesResult.Value!);
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
                return FeatureServerValueParser.TryConvertToLong(entry.Value, out objectId);
            }
        }

        return false;
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


}

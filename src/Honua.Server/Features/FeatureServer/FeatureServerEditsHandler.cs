// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.FeatureServer;

/// <summary>
/// Handler for FeatureServer edit operations with explicit geometry validation.
/// </summary>
internal sealed class FeatureServerEditsHandler(
    FeatureServerEditsDependencies dependencies,
    ILogger<FeatureServerEditsHandler> logger)
{
    private const int MaxSafeEditErrorMessageLength = 240;
    private const string InvalidFeatureDataMessage = "Invalid feature data.";
    private const string InvalidGeometryPayloadMessage = "Invalid geometry payload.";

    private readonly IResourceValidator _resourceValidator = dependencies?.ResourceValidator
        ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureReader _featureReader = dependencies.FeatureReader;
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly IFeatureServerGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly IHttpContextAccessor _httpContextAccessor = dependencies.HttpContextAccessor;
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = dependencies.FeatureChangeEventPublisher;
    private readonly ILogger<FeatureServerEditsHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        var httpContext = _httpContextAccessor.HttpContext!;
        using var scope = HonuaTelemetryScope.StartFeature(
            "applyEdits",
            HonuaTelemetry.Protocols.FeatureServer,
            layerId.ToString(CultureInfo.InvariantCulture),
            httpContext.TraceIdentifier);
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId);

        try
        {
            FeatureServerLog.ApplyEditsRequested(_logger, serviceId, layerId,
                request.Adds?.Length ?? 0,
                request.Updates?.Length ?? 0,
                request.Deletes?.Length ?? 0);

            // Validate service and layer exist
            var validationResult = await FeatureServerResourceValidationHelpers.ValidateServiceLayerAsync(
                _resourceValidator,
                serviceId,
                layerId,
                httpContext,
                _logger,
                cancellationToken);
            if (!validationResult.IsValid)
            {
                return validationResult.ErrorResult!;
            }

            var service = validationResult.Service!;
            var layer = validationResult.Layer!;
            var accessError = AccessPolicyHelpers.RequireLayerWriteAccess(httpContext, layer, service);
            if (accessError != null)
            {
                return accessError;
            }

            var rbacError = await ServiceDataEditorAuthorization.RequireServiceDataEditorAsync(
                httpContext,
                service.Name,
                cancellationToken);
            if (rbacError != null)
            {
                return rbacError;
            }

            // Validate edit limits
            var limitsValidationResult = ValidateEditLimits(request, editLimits, httpContext);
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
            var editContext = await ProcessEditOperationsAsync(request, layer, cancellationToken);

            // Handle validation errors with rollback if needed
            if (editContext.HasValidationErrors && request.RollbackOnFailure)
            {
                return CreateRollbackResponse(editContext, serviceId, layerId);
            }

            // Execute edits in the database
            var editResult = await ExecuteEdits(layer.Id, editContext, request, cancellationToken);

            if (!editResult.WasRolledBack &&
                (editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount) > 0)
            {
                await InvalidateCacheAsync(httpContext, serviceId, layerId, cancellationToken);
                await PublishFeatureChangeEventsAsync(serviceId, layerId, editContext, cancellationToken);
            }

            // Build and return final response
            var featureCount = editResult.CreatedCount + editResult.UpdatedCount + editResult.DeletedCount;
            scope.SetSuccess(featureCount);
            return CreateFinalResponse(editContext, editResult, serviceId, layerId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            FeatureServerLog.ApplyEditsFailed(_logger, serviceId, layerId, ex.Message, ex);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(httpContext, "Apply edits failed");
        }
    }

    /// <summary>
    /// Validates edit operation counts against configured limits
    /// </summary>
    private static IResult? ValidateEditLimits(ApplyEditsRequest request, Honua.Core.Configuration.EditLimits editLimits, HttpContext context)
    {
        var addCount = request.Adds?.Length ?? 0;
        var updateCount = request.Updates?.Length ?? 0;
        var deleteCount = request.Deletes?.Length ?? 0;
        var totalCount = addCount + updateCount + deleteCount;

        if (addCount > editLimits.MaxFeaturesPerEdit ||
            updateCount > editLimits.MaxFeaturesPerEdit ||
            deleteCount > editLimits.MaxFeaturesPerEdit)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Too many features in a single edit operation",
                [$"Maximum per operation: {editLimits.MaxFeaturesPerEdit}"]);
        }

        if (totalCount > editLimits.MaxEditsPerTransaction)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Too many edits in a single request",
                [$"Maximum per request: {editLimits.MaxEditsPerTransaction}"]);
        }

        return null;
    }

    /// <summary>
    /// Processes add, update, and delete operations from the request
    /// </summary>
    private async Task<EditOperationContext> ProcessEditOperationsAsync(
        ApplyEditsRequest request,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        var context = new EditOperationContext
        {
            AddResults = request.Adds is { Length: > 0 } ? new EditResult?[request.Adds.Length] : null,
            UpdateResults = request.Updates is { Length: > 0 } ? new EditResult?[request.Updates.Length] : null,
            DeleteResults = request.Deletes is { Length: > 0 } ? new EditResult?[request.Deletes.Length] : null
        };

        await ProcessAddOperationsAsync(request, context, layer, cancellationToken);
        await ProcessUpdateOperationsAsync(request, context, layer, cancellationToken);
        await ProcessDeleteOperationsAsync(request, layer.Id, context, cancellationToken);

        return context;
    }

    /// <summary>
    /// Processes add operations and tracks features to create
    /// </summary>
    private async Task ProcessAddOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        if (request.Adds == null)
            return;

        for (var i = 0; i < request.Adds.Length; i++)
        {
            try
            {
                var newFeature = await BuildFeatureFromGeoServicesAsync(request.Adds[i], 0, layer, cancellationToken);
                context.CreateFeatures.Add(newFeature);
                context.CreateIndexes.Add(i);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.AddResults![i] = CreateFailureResult(
                    code: 1000,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage));
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureAddFailed(logger, i, ex.Message, ex);
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
    private async Task ProcessUpdateOperationsAsync(
        ApplyEditsRequest request,
        EditOperationContext context,
        LayerDefinition layer,
        CancellationToken cancellationToken)
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
                var updateFeature = await BuildFeatureFromGeoServicesAsync(update, objectId, layer, cancellationToken);
                context.UpdateFeatures.Add(updateFeature);
                context.UpdateIndexes.Add(i);
                context.UpdateObjectIds.Add(objectId);
            }
            catch (ArgumentException ex)
            {
                context.HasValidationErrors = true;
                context.UpdateResults![i] = CreateFailureResult(
                    code: 1002,
                    description: SanitizeEditErrorMessage(ex.Message, InvalidFeatureDataMessage),
                    objectId: objectId);
            }
            catch (Exception ex)
            {
                FeatureServerLog.FeatureUpdateFailed(logger, i, ex.Message, ex);
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
    private async Task ProcessDeleteOperationsAsync(
        ApplyEditsRequest request,
        int layerId,
        EditOperationContext context,
        CancellationToken cancellationToken)
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
            context.DeleteFeatures.Add(await ReadDeleteFeatureSnapshotAsync(layerId, objectId, cancellationToken));
        }
    }

    private async Task<Feature?> ReadDeleteFeatureSnapshotAsync(
        int layerId,
        long objectId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _featureReader.GetAsync(layerId, objectId, cancellationToken);
        }
        catch
        {
            return null;
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

        var editResult = await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken);

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

    private static async Task InvalidateCacheAsync(
        HttpContext context,
        string serviceId,
        int layerId,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            await cacheInvalidator.InvalidateLayerAsync(serviceId, layerId, cancellationToken);
        }
    }

    private async Task PublishFeatureChangeEventsAsync(
        string serviceId,
        int layerId,
        EditOperationContext context,
        CancellationToken cancellationToken)
    {
        var requestId = _httpContextAccessor.HttpContext?.TraceIdentifier ?? "unknown";

        for (var i = 0; i < context.CreateFeatures.Count; i++)
        {
            var resultIndex = context.CreateIndexes[i];
            var result = context.AddResults?[resultIndex];
            if (result is not { Success: true, ObjectId: { } objectId })
            {
                continue;
            }

            var (createEnv, createProps) = FeatureChangeEventEnrichment.FromFeature(context.CreateFeatures[i]);
            await _featureChangeEventPublisher.PublishAsync(
                new FeatureChangeEventRequest
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    ObjectId = objectId,
                    Operation = "create",
                    Protocol = HonuaTelemetry.Protocols.FeatureServer,
                    RequestId = requestId,
                    GeometryEnvelope = createEnv,
                    PropertiesJson = createProps
                },
                cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < context.UpdateFeatures.Count; i++)
        {
            var resultIndex = context.UpdateIndexes[i];
            var result = context.UpdateResults?[resultIndex];
            if (result is not { Success: true, ObjectId: { } objectId })
            {
                continue;
            }

            var (updateEnv, updateProps) = FeatureChangeEventEnrichment.FromFeature(context.UpdateFeatures[i]);
            await _featureChangeEventPublisher.PublishAsync(
                new FeatureChangeEventRequest
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    ObjectId = objectId,
                    Operation = "update",
                    Protocol = HonuaTelemetry.Protocols.FeatureServer,
                    RequestId = requestId,
                    GeometryEnvelope = updateEnv,
                    PropertiesJson = updateProps
                },
                cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < context.DeleteIndexes.Count; i++)
        {
            var resultIndex = context.DeleteIndexes[i];
            var result = context.DeleteResults?[resultIndex];
            if (result is not { Success: true, ObjectId: { } objectId })
            {
                continue;
            }

            var deleteFeature = context.DeleteFeatures.Count > i ? context.DeleteFeatures[i] : null;
            var (deleteEnv, deleteProps) = FeatureChangeEventEnrichment.FromFeature(deleteFeature);
            await _featureChangeEventPublisher.PublishAsync(
                new FeatureChangeEventRequest
                {
                    ServiceId = serviceId,
                    LayerId = layerId,
                    ObjectId = objectId,
                    Operation = "delete",
                    Protocol = HonuaTelemetry.Protocols.FeatureServer,
                    RequestId = requestId,
                    GeometryEnvelope = deleteEnv,
                    PropertiesJson = deleteProps
                },
                cancellationToken).ConfigureAwait(false);
        }
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
        public List<Feature?> DeleteFeatures { get; } = new();
        public List<int> DeleteIndexes { get; } = new();
        public bool HasValidationErrors { get; set; }
    }

    private async Task<Feature> BuildFeatureFromGeoServicesAsync(
        GeoServicesFeature feature,
        long objectId,
        LayerDefinition layer,
        CancellationToken cancellationToken)
    {
        byte[]? geometry = null;
        if (feature.Geometry != null)
        {
            // Layer 1: Validate Esri JSON input
            var esriValidation = _geometryServices.ValidateEsriJson(feature.Geometry);
            if (!esriValidation.IsValid)
            {
                var errorMessages = string.Join("; ", esriValidation.Errors.Select(e => e.Message));
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {errorMessages}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            var layerSrid = layer.SpatialReference.Wkid;
            var geometrySrid = feature.Geometry.SpatialReference?.Wkid
                ?? feature.Geometry.SpatialReference?.LatestWkid;
            if (geometrySrid.HasValue && geometrySrid.Value != layerSrid)
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry spatial reference {geometrySrid.Value} does not match layer SRID {layerSrid}.",
                    "Geometry spatial reference does not match layer SRID.");
                throw new ArgumentException(safeError);
            }

            geometrySrid ??= layerSrid;
            try
            {
                geometry = GeoServicesGeometryConverter.ConvertGeoServicesGeometryToWkb(feature.Geometry, geometrySrid);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException(
                    SanitizeEditErrorMessage(ex.Message, InvalidGeometryPayloadMessage),
                    ex);
            }

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometry, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                var safeError = SanitizeEditErrorMessage(
                    $"Geometry validation failed: {geometryValidation.ErrorMessage}",
                    "Geometry validation failed.");
                throw new ArgumentException(safeError);
            }

            geometry = geometryValidation.Geometry;
        }

        var attributesResult = _mutationValidator.ValidateAttributes(
            layer,
            feature.Attributes,
            ValidationExtensions.AttributeValidationMode.GeoServices);
        if (!attributesResult.IsValid)
        {
            throw new ArgumentException(
                SanitizeEditErrorMessage(attributesResult.ErrorMessage, "Invalid attributes."));
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
            if (string.Equals(entry.Key, FieldNames.ObjectId, StringComparison.OrdinalIgnoreCase))
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
            SanitizeEditErrorMessage(result.ErrorMessage, "Operation failed"),
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

    private static string SanitizeEditErrorMessage(string? message, string fallback)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return fallback;
        }

        var trimmed = message.Trim();
        if (trimmed.Length > MaxSafeEditErrorMessageLength || ContainsUnsafeEditMessagePattern(trimmed))
        {
            return fallback;
        }

        return trimmed;
    }

    private static bool ContainsUnsafeEditMessagePattern(string message)
    {
        return message.Contains('\r') ||
               message.Contains('\n') ||
               message.Contains("BytePositionInLine", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("LineNumber", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("System.", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("StackTrace", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SQLSTATE", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("password", StringComparison.OrdinalIgnoreCase);
    }


}

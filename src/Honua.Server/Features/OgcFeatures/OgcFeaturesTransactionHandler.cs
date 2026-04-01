// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcFeatures.Models;
using Honua.Server.Features.OgcFeatures.Services;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.OgcFeatures;

/// <summary>
/// Handler for OGC Features transaction operations and complex multi-step operations.
/// </summary>
internal sealed partial class OgcFeaturesTransactionHandler(
    OgcFeaturesTransactionDependencies dependencies,
    ILogger<OgcFeaturesTransactionHandler> logger)
{
    private readonly IFeatureReader _featureReader = dependencies?.FeatureReader ?? throw new ArgumentNullException(nameof(dependencies));
    private readonly IFeatureWriter _featureWriter = dependencies.FeatureWriter;
    private readonly ICrsRegistry _crsRegistry = dependencies.CrsRegistry;
    private readonly OgcFeaturesGeometryServices _geometryServices = dependencies.GeometryServices;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly IFeatureChangeEventPublisher _featureChangeEventPublisher = dependencies.FeatureChangeEventPublisher;
    private readonly ILogger<OgcFeaturesTransactionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles batch feature operations in a single transaction.
    /// </summary>
    public async Task<IResult> HandleBatchOperationAsync(
        string collectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "batch");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
                context.Request,
                layer,
                _crsRegistry,
                cancellationToken);
            if (!crsResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResult.Error ?? "Invalid Content-Crs header.");
            }

            var inputCrs = crsResult.Definition;

            var (batchRequest, requestError) = await ReadBatchRequestAsync(context, cancellationToken);
            if (batchRequest == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid batch request payload.");
            }

            const int maxBatchOperations = 1000;
            if (batchRequest.Operations.Count > maxBatchOperations)
            {
                return StandardErrorHelpers.CreateBadRequest(context,
                    $"Batch request exceeds maximum of {maxBatchOperations} operations.");
            }

            var preparedBatch = await PrepareBatchOperationsAsync(
                layerId,
                layer,
                batchRequest,
                inputCrs,
                cancellationToken).ConfigureAwait(false);

            List<BatchOperationResult> results;
            var hasErrors = false;

            if (preparedBatch.ShortCircuitResults is { Count: > 0 })
            {
                results = preparedBatch.ShortCircuitResults;
                hasErrors = true;
            }
            else if (preparedBatch.EditBatch.IsEmpty)
            {
                results = [];
            }
            else
            {
                var editResult = await _featureWriter.ApplyEditsAsync(
                    layerId,
                    preparedBatch.EditBatch,
                    cancellationToken).ConfigureAwait(false);

                results = MapBatchEditResults(batchRequest.Operations.Count, preparedBatch.PreparedOperations, editResult);
                hasErrors = results.Any(result => !result.IsSuccess);
            }

            if (!hasErrors)
            {
                var serviceId = await ResolveServiceIdAsync(context, layerId, cancellationToken);
                for (var index = 0; index < results.Count && index < batchRequest.Operations.Count; index++)
                {
                    var operation = batchRequest.Operations[index];
                    var result = results[index];
                    if (!TryGetBatchEventOperation(operation, result, out var eventOperation, out var objectId))
                    {
                        continue;
                    }

                    await _featureChangeEventPublisher.PublishAsync(
                        new FeatureChangeEventRequest
                        {
                            ServiceId = serviceId,
                            LayerId = layerId,
                            ObjectId = objectId,
                            Operation = eventOperation,
                            Protocol = HonuaTelemetry.Protocols.OgcFeatures,
                            RequestId = $"{context.TraceIdentifier}:{operation.Id ?? "batch"}"
                        },
                        cancellationToken).ConfigureAwait(false);
                }
            }

            var response = new BatchOperationResponse
            {
                Results = results,
                HasErrors = hasErrors,
                ProcessedCount = results.Count,
                SuccessCount = results.Count(r => r.IsSuccess)
            };

            var statusCode = hasErrors ? 207 : 200; // 207 Multi-Status for partial success
            if (results.Any(result => result.IsSuccess))
            {
                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
            }
            context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
            HonuaTelemetry.SetSuccess(activity, response.SuccessCount);
            return Results.Json(response, OgcJsonContext.Default.BatchOperationResponse, statusCode: statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BatchTransactionFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while processing the batch operation.");
        }
    }

    /// <summary>
    /// Handles feature replacement with optimistic concurrency control.
    /// </summary>
    public async Task<IResult> HandleReplaceFeatureAsync(
        string collectionId,
        string featureId,
        string? ifMatch,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "replace");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            // Check if feature exists and validate ETag if provided
            var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (existing == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = GenerateETag(existing.Value);
                if (!string.Equals(ifMatch.Trim('"'), etag, StringComparison.Ordinal))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }
            }

            var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
                context.Request,
                layer,
                requestFeature,
                _crsRegistry,
                _geometryServices,
                _mutationValidator,
                objectId,
                cancellationToken);
            if (!buildResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    buildResult.ErrorMessage ?? "Invalid feature payload.");
            }

            var inputCrs = buildResult.InputCrs!.Value;
            var feature = buildResult.Feature
                ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

            try
            {
                var updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
                var newETag = GenerateETag(updated);

                context.Response.Headers.ETag = $"\"{newETag}\"";

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    FormattableString.Invariant($"{updated.Id}"),
                    MediaTypes.GeoJson);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);

                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
                var serviceId = await ResolveServiceIdAsync(context, layerId, cancellationToken);
                await _featureChangeEventPublisher.PublishAsync(
                    new FeatureChangeEventRequest
                    {
                        ServiceId = serviceId,
                        LayerId = layerId,
                        ObjectId = updated.Id,
                        Operation = "update",
                        Protocol = HonuaTelemetry.Protocols.OgcFeatures,
                        RequestId = context.TraceIdentifier
                    },
                    cancellationToken).ConfigureAwait(false);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.ReplaceFeatureConflict(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
            catch (ResourceNotFoundException)
            {
                Log.ReplaceFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException ex)
            {
                Log.ReplaceFeatureFailed(_logger, collectionId, ex);
                return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while replacing the feature.");
            }
            catch (ArgumentException ex)
            {
                Log.ReplaceFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.ReplaceFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while replacing the feature.");
        }
    }

    /// <summary>
    /// Handles partial feature updates with merge semantics and optimistic concurrency control.
    /// </summary>
    public async Task<IResult> HandlePatchFeatureAsync(
        string collectionId,
        string featureId,
        string? ifMatch,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var layerValidation = await LayerValidationHelpers.ValidateCollectionWriteAccessAsync(
                context, collectionId, cancellationToken);
            if (!layerValidation.IsValid)
            {
                return layerValidation.ErrorResult!;
            }
            var layer = layerValidation.Layer!;
            var layerId = layer.Id;

            using var activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit, ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OgcFeatures);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "patch");
            activity?.SetTag(HonuaTelemetry.Tags.LayerId, layerId);

            if (!long.TryParse(featureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
            if (existing == null)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = GenerateETag(existing.Value);
                if (!string.Equals(ifMatch.Trim('"'), etag, StringComparison.Ordinal))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }
            }

            var (patchRequest, patchError) = await ReadPatchRequestAsync(context, cancellationToken);
            if (patchRequest == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, patchError ?? "Invalid patch payload.");
            }

            if (patchRequest.FeatureId.HasValue && patchRequest.FeatureId.Value != objectId)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Payload feature ID '{patchRequest.FeatureId.Value}' does not match route feature ID '{featureId}'.");
            }

            var crsResult = await OgcRequestCrsResolver.TryResolveInputCrsAsync(
                context.Request,
                layer,
                _crsRegistry,
                cancellationToken);
            if (!crsResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(context, crsResult.Error ?? "Invalid Content-Crs header.");
            }

            var inputCrs = crsResult.Definition;

            byte[]? geometryWkb = existing.Value.Geometry;
            if (patchRequest.HasGeometry)
            {
                if (patchRequest.Geometry == null)
                {
                    geometryWkb = null;
                }
                else
                {
                    var wkbResult = _geometryServices.TryCreateWkbFromGeoJson(
                        patchRequest.Geometry,
                        inputCrs.Srid,
                        inputCrs.AxisOrder);
                    if (!wkbResult.IsSuccess)
                    {
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            wkbResult.ErrorMessage ?? "Invalid geometry payload.");
                    }

                    geometryWkb = wkbResult.Wkb;
                }
            }

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometryWkb, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Invalid geometry: {geometryValidation.ErrorMessage}");
            }

            var attributesBuilder = existing.Value.Attributes.ToBuilder();
            if (patchRequest.HasProperties)
            {
                if (patchRequest.Properties == null)
                {
                    attributesBuilder.Clear();
                }
                else
                {
                    foreach (var (key, value) in patchRequest.Properties)
                    {
                        if (value == null)
                        {
                            attributesBuilder.Remove(key);
                        }
                        else
                        {
                            attributesBuilder[key] = value;
                        }
                    }
                }
            }

            var attributesResult = _mutationValidator.ValidateAttributes(
                layer,
                attributesBuilder.ToImmutable(),
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            var feature = Feature.Create(
                objectId,
                geometryValidation.Geometry,
                attributesResult.Value!);

            try
            {
                var updated = await _featureWriter.UpdateAsync(layerId, feature, cancellationToken);
                var newETag = GenerateETag(updated);
                context.Response.Headers.ETag = $"\"{newETag}\"";

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    FormattableString.Invariant($"{updated.Id}"),
                    MediaTypes.GeoJson);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(updated, inputCrs.AxisOrder, updateLinks);

                await OgcFeaturesUtilities.InvalidateLayerCacheAsync(context, layerId, cancellationToken);
                var serviceId = await ResolveServiceIdAsync(context, layerId, cancellationToken);
                await _featureChangeEventPublisher.PublishAsync(
                    new FeatureChangeEventRequest
                    {
                        ServiceId = serviceId,
                        LayerId = layerId,
                        ObjectId = updated.Id,
                        Operation = "update",
                        Protocol = HonuaTelemetry.Protocols.OgcFeatures,
                        RequestId = context.TraceIdentifier
                    },
                    cancellationToken).ConfigureAwait(false);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.PatchFeatureConflict(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
            catch (ResourceNotFoundException)
            {
                Log.PatchFeatureNotFound(_logger, collectionId, featureId);
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException ex)
            {
                Log.PatchFeatureFailed(_logger, collectionId, ex);
                return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while patching the feature.");
            }
            catch (ArgumentException ex)
            {
                Log.PatchFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.PatchFeatureFailed(_logger, collectionId, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while patching the feature.");
        }
    }

    private async Task<PreparedBatchValidationResult> PrepareBatchOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        BatchPreparationState state,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (operation.Type.ToUpperInvariant())
            {
                case "CREATE":
                    return await PrepareCreateOperationAsync(layerId, layer, operation, inputCrs, cancellationToken);
                case "UPDATE":
                    return await PrepareUpdateOperationAsync(layerId, layer, operation, state, inputCrs, cancellationToken);
                case "DELETE":
                    return await PrepareDeleteOperationAsync(layerId, operation, state, cancellationToken);
                default:
                    return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                        operation.Id,
                        $"Unsupported operation type: {operation.Type}",
                        400));
            }
        }
        catch (Exception ex)
        {
            Log.BatchOperationFailed(_logger, layerId.ToString(), operation.Id ?? "unknown", ex);
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                "An error occurred processing the operation.",
                500));
        }
    }

    private async Task<PreparedBatchValidationResult> PrepareCreateOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        if (operation.Feature == null)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                "Feature data is required for create operation.",
                400));
        }

        var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            layer,
            operation.Feature,
            inputCrs,
            _geometryServices,
            _mutationValidator,
            objectId: 0,
            cancellationToken);
        if (!buildResult.IsValid)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                buildResult.ErrorMessage ?? "Invalid feature payload.",
                400));
        }

        var feature = buildResult.Feature
            ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");

        return PreparedBatchValidationResult.Success(new PreparedBatchOperation(
            OperationKind: BatchOperationKind.Create,
            Operation: operation,
            Feature: feature,
            ObjectId: null));
    }

    private async Task<PreparedBatchValidationResult> PrepareUpdateOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperation operation,
        BatchPreparationState state,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        if (operation.Feature == null || string.IsNullOrWhiteSpace(operation.FeatureId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                "Feature data and feature ID are required for update operation.",
                400));
        }

        if (!long.TryParse(operation.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Invalid feature ID: {operation.FeatureId}",
                400));
        }

        if (state.DeletedObjectIds.Contains(objectId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        if (!existing.HasValue)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        var buildResult = await OgcFeatureMutationHelpers.TryBuildFeatureAsync(
            layer,
            operation.Feature,
            inputCrs,
            _geometryServices,
            _mutationValidator,
            objectId,
            cancellationToken);
        if (!buildResult.IsValid)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                buildResult.ErrorMessage ?? "Invalid feature payload.",
                400));
        }

        var feature = buildResult.Feature
            ?? throw new InvalidOperationException("Feature build result was missing the feature payload.");
        return PreparedBatchValidationResult.Success(new PreparedBatchOperation(
            OperationKind: BatchOperationKind.Update,
            Operation: operation,
            Feature: feature,
            ObjectId: objectId));
    }

    private async Task<PreparedBatchValidationResult> PrepareDeleteOperationAsync(
        int layerId,
        BatchOperation operation,
        BatchPreparationState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.FeatureId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                "Feature ID is required for delete operation.",
                400));
        }

        if (!long.TryParse(operation.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objectId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Invalid feature ID: {operation.FeatureId}",
                400));
        }

        if (state.DeletedObjectIds.Contains(objectId))
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        var existing = await _featureReader.GetAsync(layerId, objectId, cancellationToken).ConfigureAwait(false);
        if (!existing.HasValue)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        return PreparedBatchValidationResult.Success(new PreparedBatchOperation(
            OperationKind: BatchOperationKind.Delete,
            Operation: operation,
            Feature: null,
            ObjectId: objectId));
    }

    private async Task<PreparedBatchPlan> PrepareBatchOperationsAsync(
        int layerId,
        LayerDefinition layer,
        BatchRequest batchRequest,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        var preparedOperations = ImmutableArray.CreateBuilder<PreparedBatchOperation>(batchRequest.Operations.Count);
        var validationResults = new BatchOperationResult?[batchRequest.Operations.Count];
        var validationState = new BatchPreparationState();
        var processedCount = batchRequest.Operations.Count;
        var validationFailed = false;

        for (var index = 0; index < batchRequest.Operations.Count; index++)
        {
            var operation = batchRequest.Operations[index];
            if (validationFailed)
            {
                validationResults[index] = CreateRolledBackBatchFailure(operation.Id);
                continue;
            }

            var prepared = await PrepareBatchOperationAsync(
                layerId,
                layer,
                operation,
                validationState,
                inputCrs,
                cancellationToken).ConfigureAwait(false);

            if (!prepared.IsValid)
            {
                validationFailed = true;
                validationResults[index] = prepared.ErrorResult!;

                foreach (var priorOperation in preparedOperations)
                {
                    validationResults[priorOperation.Index] = CreateRolledBackBatchFailure(priorOperation.Operation.Id);
                }

                if (batchRequest.FailFast)
                {
                    processedCount = index + 1;
                    break;
                }

                continue;
            }

            var preparedOperation = prepared.Operation! with { Index = index };
            preparedOperations.Add(preparedOperation);
            ApplyPreparationState(validationState, preparedOperation);
        }

        if (validationFailed)
        {
            var results = validationResults
                .Take(processedCount)
                .Select(static result => result ?? throw new InvalidOperationException("Validation result was missing."))
                .ToList();
            return new PreparedBatchPlan(default, preparedOperations.ToImmutable(), results);
        }

        var editBatch = new FeatureEditBatch
        {
            RollbackOnFailure = true,
            Operations = preparedOperations
                .Select(static operation => operation.OperationKind switch
                {
                    BatchOperationKind.Create => FeatureEditOperation.Create(
                        operation.Feature ?? throw new InvalidOperationException("Prepared create operation was missing a feature.")),
                    BatchOperationKind.Update => FeatureEditOperation.Update(
                        operation.Feature ?? throw new InvalidOperationException("Prepared update operation was missing a feature.")),
                    BatchOperationKind.Delete => FeatureEditOperation.Delete(
                        operation.ObjectId ?? throw new InvalidOperationException("Prepared delete operation was missing an object ID.")),
                    _ => throw new InvalidOperationException($"Unsupported batch operation kind {operation.OperationKind}.")
                })
                .ToImmutableArray(),
            Creates = preparedOperations
                .Where(static operation => operation.OperationKind == BatchOperationKind.Create)
                .Select(static operation => operation.Feature ?? throw new InvalidOperationException("Prepared create operation was missing a feature."))
                .ToImmutableArray(),
            Updates = preparedOperations
                .Where(static operation => operation.OperationKind == BatchOperationKind.Update)
                .Select(static operation => operation.Feature ?? throw new InvalidOperationException("Prepared update operation was missing a feature."))
                .ToImmutableArray(),
            Deletes = preparedOperations
                .Where(static operation => operation.OperationKind == BatchOperationKind.Delete)
                .Select(static operation => operation.ObjectId!.Value)
                .ToImmutableArray()
        };

        return new PreparedBatchPlan(editBatch, preparedOperations.ToImmutable(), null);
    }

    private static List<BatchOperationResult> MapBatchEditResults(
        int operationCount,
        ImmutableArray<PreparedBatchOperation> preparedOperations,
        FeatureEditResult editResult)
    {
        var results = new BatchOperationResult[operationCount];
        var createIndex = 0;
        var updateIndex = 0;
        var deleteIndex = 0;

        foreach (var operation in preparedOperations)
        {
            results[operation.Index] = operation.OperationKind switch
            {
                BatchOperationKind.Create => MapCreateEditResult(
                    operation.Operation,
                    GetEditResult(editResult.CreateResults, createIndex++)),
                BatchOperationKind.Update => MapUpdateEditResult(
                    operation.Operation,
                    operation.ObjectId,
                    GetEditResult(editResult.UpdateResults, updateIndex++)),
                BatchOperationKind.Delete => MapDeleteEditResult(
                    operation.Operation,
                    operation.ObjectId,
                    GetEditResult(editResult.DeleteResults, deleteIndex++)),
                _ => throw new InvalidOperationException($"Unsupported batch operation kind {operation.OperationKind}.")
            };
        }

        return results.ToList();
    }

    private static EditOperationResult GetEditResult(ImmutableArray<EditOperationResult> results, int index)
        => index < results.Length
            ? results[index]
            : EditOperationResult.Failure("Operation result was missing.", errorCode: 500);

    private static BatchOperationResult MapCreateEditResult(BatchOperation operation, EditOperationResult editResult)
    {
        if (editResult.IsSuccess && editResult.ObjectId.HasValue)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = editResult.ObjectId.Value.ToString(CultureInfo.InvariantCulture),
                StatusCode = 201
            };
        }

        return CreateBatchFailure(operation.Id, editResult.ErrorMessage ?? "Create operation failed.", DetermineBatchFailureStatus(editResult));
    }

    private static BatchOperationResult MapUpdateEditResult(
        BatchOperation operation,
        long? objectId,
        EditOperationResult editResult)
    {
        if (editResult.IsSuccess)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = (editResult.ObjectId ?? objectId ?? 0).ToString(CultureInfo.InvariantCulture),
                StatusCode = 200
            };
        }

        return CreateBatchFailure(operation.Id, editResult.ErrorMessage ?? "Update operation failed.", DetermineBatchFailureStatus(editResult));
    }

    private static BatchOperationResult MapDeleteEditResult(
        BatchOperation operation,
        long? objectId,
        EditOperationResult editResult)
    {
        if (editResult.IsSuccess)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = (objectId ?? editResult.ObjectId ?? 0).ToString(CultureInfo.InvariantCulture),
                StatusCode = 204
            };
        }

        return CreateBatchFailure(operation.Id, editResult.ErrorMessage ?? "Delete operation failed.", DetermineBatchFailureStatus(editResult));
    }

    private static int DetermineBatchFailureStatus(EditOperationResult editResult)
    {
        if (string.Equals(editResult.ErrorMessage, "Operation rolled back.", StringComparison.Ordinal))
        {
            return 424;
        }

        if (!string.IsNullOrWhiteSpace(editResult.ErrorMessage) &&
            editResult.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return 404;
        }

        return editResult.ErrorCode >= 500 ? 500 : 400;
    }

    private static BatchOperationResult CreateBatchFailure(string? operationId, string message, int statusCode)
        => new()
        {
            OperationId = operationId,
            IsSuccess = false,
            ErrorMessage = message,
            StatusCode = statusCode
        };

    private static BatchOperationResult CreateRolledBackBatchFailure(string? operationId)
        => CreateBatchFailure(operationId, "Operation rolled back.", 424);

    private static void ApplyPreparationState(BatchPreparationState state, PreparedBatchOperation operation)
    {
        if (operation.OperationKind == BatchOperationKind.Delete && operation.ObjectId.HasValue)
        {
            state.DeletedObjectIds.Add(operation.ObjectId.Value);
        }
    }

    private static async Task<(BatchRequest? Request, string? Error)> ReadBatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength == 0)
            {
                return (null, "Request body is required.");
            }

            var request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                OgcJsonContext.Default.BatchRequest,
                cancellationToken);
            return request == null
                ? (null, "Invalid batch request payload.")
                : (request, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static async Task<(PatchRequest? Request, string? Error)> ReadPatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            if (context.Request.ContentLength == 0)
            {
                return (null, "Request body is required.");
            }

            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Patch payload must be a JSON object.");
            }

            var root = document.RootElement;
            if (root.TryGetProperty("type", out var typeProperty))
            {
                if (typeProperty.ValueKind != JsonValueKind.String ||
                    !string.Equals(typeProperty.GetString(), "Feature", StringComparison.OrdinalIgnoreCase))
                {
                    return (null, "GeoJSON 'type' must be 'Feature' when provided.");
                }
            }

            long? payloadId = null;
            if (root.TryGetProperty("id", out var idProperty))
            {
                if (!TryReadLong(idProperty, out var parsedId))
                {
                    return (null, "GeoJSON 'id' must be an integer when provided.");
                }

                payloadId = parsedId;
            }

            Dictionary<string, object?>? properties = null;
            var hasProperties = root.TryGetProperty("properties", out var propertiesProperty);
            if (hasProperties)
            {
                if (propertiesProperty.ValueKind == JsonValueKind.Null)
                {
                    properties = null;
                }
                else if (propertiesProperty.ValueKind == JsonValueKind.Object)
                {
                    properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in propertiesProperty.EnumerateObject())
                    {
                        properties[property.Name] = JsonElementConverter.ConvertToObject(property.Value);
                    }
                }
                else
                {
                    return (null, "GeoJSON 'properties' must be an object or null when provided.");
                }
            }

            SimpleGeoJsonGeometry? geometry = null;
            var hasGeometry = root.TryGetProperty("geometry", out var geometryProperty);
            if (hasGeometry)
            {
                if (geometryProperty.ValueKind == JsonValueKind.Null)
                {
                    geometry = null;
                }
                else if (geometryProperty.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        geometry = JsonSerializer.Deserialize(
                            geometryProperty.GetRawText(),
                            OgcJsonContext.Default.SimpleGeoJsonGeometry);
                    }
                    catch (JsonException)
                    {
                        return (null, "Invalid GeoJSON geometry payload.");
                    }

                    if (geometry == null || string.IsNullOrWhiteSpace(geometry.Type))
                    {
                        return (null, "GeoJSON geometry must include a 'type' value.");
                    }
                }
                else
                {
                    return (null, "GeoJSON 'geometry' must be an object or null when provided.");
                }
            }

            return (new PatchRequest(payloadId, hasGeometry, geometry, hasProperties, properties), null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static bool TryReadLong(JsonElement value, out long result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out result))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryGetBatchEventOperation(
        BatchOperation operation,
        BatchOperationResult result,
        out string eventOperation,
        out long objectId)
    {
        eventOperation = "update";
        objectId = 0;

        if (!result.IsSuccess)
        {
            return false;
        }

        var normalized = operation.Type.Trim().ToUpperInvariant();
        switch (normalized)
        {
            case "CREATE":
                if (!long.TryParse(result.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId))
                {
                    return false;
                }

                eventOperation = "create";
                return true;
            case "UPDATE":
                if (!long.TryParse(result.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId))
                {
                    return false;
                }

                eventOperation = "update";
                return true;
            case "DELETE":
                if (!long.TryParse(operation.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId))
                {
                    return false;
                }

                eventOperation = "delete";
                return true;
            default:
                return false;
        }
    }

    private enum BatchOperationKind
    {
        Create,
        Update,
        Delete
    }

    private sealed record PreparedBatchOperation(
        BatchOperationKind OperationKind,
        BatchOperation Operation,
        Feature? Feature,
        long? ObjectId)
    {
        public int Index { get; init; }
    }

    private sealed record PreparedBatchValidationResult(
        PreparedBatchOperation? Operation,
        BatchOperationResult? ErrorResult)
    {
        public bool IsValid => Operation != null;

        public static PreparedBatchValidationResult Success(PreparedBatchOperation operation)
            => new(operation, null);

        public static PreparedBatchValidationResult Failure(BatchOperationResult errorResult)
            => new(null, errorResult);
    }

    private sealed record PreparedBatchPlan(
        FeatureEditBatch EditBatch,
        ImmutableArray<PreparedBatchOperation> PreparedOperations,
        List<BatchOperationResult>? ShortCircuitResults);

    private sealed class BatchPreparationState
    {
        public HashSet<long> DeletedObjectIds { get; } = [];
    }

    private sealed record PatchRequest(
        long? FeatureId,
        bool HasGeometry,
        SimpleGeoJsonGeometry? Geometry,
        bool HasProperties,
        Dictionary<string, object?>? Properties);

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
    {
        var geometry = _geometryServices.ConvertWkbToSimpleGeometry(feature.Geometry, axisOrder);
        return feature.ToGeoJsonBase().ToOgcGeoJsonFeature(geometry, links);
    }

    private static async Task<string> ResolveServiceIdAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        var serviceId = await LayerValidationHelpers.ResolvePrimaryServiceNameAsync(
            context,
            layerId,
            ServiceProtocols.OgcFeatures,
            cancellationToken);

        return serviceId ?? layerId.ToString(CultureInfo.InvariantCulture);
    }

    private static string GenerateETag(Feature feature)
    {
        // Generate an ETag based on a hash of the full feature content
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("id", feature.Id);
        if (feature.Geometry != null)
        {
            writer.WriteBase64String("g", feature.Geometry);
        }

        if (feature.Attributes != null)
        {
            writer.WriteStartObject("a");
            foreach (var kvp in feature.Attributes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(kvp.Key);
                WriteAttributeValue(writer, kvp.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.Flush();

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream.ToArray()))[..16];
    }

    private static void WriteAttributeValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case float f:
                writer.WriteNumberValue(f);
                break;
            case decimal dec:
                writer.WriteNumberValue(dec);
                break;
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O"));
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O"));
                break;
            case JsonElement je:
                je.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 5220, Level = LogLevel.Error, Message = "OGC batch transaction failed for collection {CollectionId}")]
        public static partial void BatchTransactionFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5221, Level = LogLevel.Error, Message = "OGC batch operation failed for collection {CollectionId}, operation {OperationId}")]
        public static partial void BatchOperationFailed(ILogger logger, string collectionId, string operationId, Exception exception);

        [LoggerMessage(EventId = 5222, Level = LogLevel.Error, Message = "OGC replace feature failed for collection {CollectionId}")]
        public static partial void ReplaceFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5223, Level = LogLevel.Error, Message = "OGC patch feature failed for collection {CollectionId}")]
        public static partial void PatchFeatureFailed(ILogger logger, string collectionId, Exception exception);

        [LoggerMessage(EventId = 5224, Level = LogLevel.Warning, Message = "OGC replace feature conflict for collection {CollectionId}, feature {FeatureId}")]
        public static partial void ReplaceFeatureConflict(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5225, Level = LogLevel.Warning, Message = "OGC replace feature not found for collection {CollectionId}, feature {FeatureId}")]
        public static partial void ReplaceFeatureNotFound(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5226, Level = LogLevel.Warning, Message = "OGC replace feature invalid payload for collection {CollectionId}, feature {FeatureId}: {Reason}")]
        public static partial void ReplaceFeatureInvalidPayload(ILogger logger, string collectionId, string featureId, string reason);

        [LoggerMessage(EventId = 5227, Level = LogLevel.Warning, Message = "OGC patch feature conflict for collection {CollectionId}, feature {FeatureId}")]
        public static partial void PatchFeatureConflict(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5228, Level = LogLevel.Warning, Message = "OGC patch feature not found for collection {CollectionId}, feature {FeatureId}")]
        public static partial void PatchFeatureNotFound(ILogger logger, string collectionId, string featureId);

        [LoggerMessage(EventId = 5229, Level = LogLevel.Warning, Message = "OGC patch feature invalid payload for collection {CollectionId}, feature {FeatureId}: {Reason}")]
        public static partial void PatchFeatureInvalidPayload(ILogger logger, string collectionId, string featureId, string reason);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using BatchOperationModel = Honua.Server.Features.Protocols.Ogc.Api.Features.Models.BatchOperation;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.Ogc.Api.Features;

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
    private readonly IEditParameterAdapter<OgcFeaturesEditRequest> _editParameterAdapter = dependencies.EditParameterAdapter;
    private readonly IEditProcessor _editProcessor = dependencies.EditProcessor;
    private readonly IQueryProcessor _queryProcessor = dependencies.QueryProcessor;
    private readonly IETagService _etagService = dependencies.ETagService;
    private readonly FeatureMutationValidator _mutationValidator = dependencies.MutationValidator;
    private readonly FeatureMutationEventService _mutationEventService = dependencies.MutationEventService;
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

            var contentTypeError = OgcFeaturePayloadReader.ValidateJsonContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

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
            long?[]? objectIdsByOperationIndex = null;
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
                var editResult = await ExecuteEditAsync(
                    context,
                    layerId,
                    layer,
                    new OgcFeaturesEditRequest
                    {
                        Operation = OgcFeaturesEditOperation.Batch,
                        RollbackOnFailure = true,
                        BatchOperations = preparedBatch.PreparedOperations
                            .Select(static operation => new OgcFeaturesBatchEditOperation
                            {
                                Operation = operation.OperationKind switch
                                {
                                    BatchOperationKind.Create => OgcFeaturesEditOperation.Create,
                                    BatchOperationKind.Update => OgcFeaturesEditOperation.Replace,
                                    BatchOperationKind.Delete => OgcFeaturesEditOperation.Delete,
                                    _ => throw new InvalidOperationException($"Unsupported batch operation kind {operation.OperationKind}.")
                                },
                                Feature = operation.Feature,
                                ObjectId = operation.ObjectId
                            })
                            .ToImmutableArray()
                    },
                    cancellationToken).ConfigureAwait(false);

                objectIdsByOperationIndex = MapBatchObjectIdsByOperationIndex(
                    batchRequest.Operations.Count,
                    preparedBatch.PreparedOperations,
                    editResult);
                results = await MapBatchEditResultsAsync(
                    batchRequest.Operations.Count,
                    layerId,
                    layer,
                    inputCrs,
                    preparedBatch.PreparedOperations,
                    editResult,
                    cancellationToken).ConfigureAwait(false);
                hasErrors = results.Any(result => !result.IsSuccess);
            }

            if (!hasErrors)
            {
                PreparedBatchOperation?[]? preparedOperationsByIndex = null;
                if (!preparedBatch.PreparedOperations.IsDefaultOrEmpty)
                {
                    preparedOperationsByIndex = new PreparedBatchOperation?[batchRequest.Operations.Count];
                    foreach (var preparedOperation in preparedBatch.PreparedOperations)
                    {
                        preparedOperationsByIndex[preparedOperation.Index] = preparedOperation;
                    }
                }

                for (var index = 0; index < results.Count && index < batchRequest.Operations.Count; index++)
                {
                    var operation = batchRequest.Operations[index];
                    var result = results[index];
                    var preparedOperation = preparedOperationsByIndex?[index];
                    if (!TryGetBatchEventOperation(
                            operation,
                            result,
                            preparedOperation,
                            objectIdsByOperationIndex?[index],
                            out var eventOperation,
                            out var objectId))
                    {
                        continue;
                    }

                    await _mutationEventService.PublishAsync(
                        context,
                        layerId,
                        objectId,
                        eventOperation,
                        HonuaTelemetry.Protocols.OgcFeatures,
                        CancellationToken.None,
                        serviceProtocol: ServiceProtocols.OgcFeatures,
                        requestId: $"{context.TraceIdentifier}:{operation.Id ?? "batch"}",
                        mutationFeature: preparedOperation?.OperationKind == BatchOperationKind.Delete
                            ? null
                            : preparedOperation?.Feature,
                        geometryChanged: preparedOperation?.Feature?.Geometry is { Length: > 0 }).ConfigureAwait(false);
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
                await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
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
            HonuaTelemetry.RecordException(Activity.Current, ex);
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

            var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
                _featureReader,
                _queryProcessor,
                layer,
                featureId,
                cancellationToken).ConfigureAwait(false);
            if (!resolvedFeature.HasValue)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var objectId = resolvedFeature.Value.ObjectId;
            var existing = resolvedFeature.Value.Feature;
            var expectedFeatureId = OgcFeatureIdentifierResolver.FormatPublicId(existing, layer);

            var contentTypeError = OgcFeaturePayloadReader.ValidateFeatureContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

            var (requestFeature, requestError) = await OgcFeaturePayloadReader.ReadGeoJsonFeatureAsync(context, cancellationToken);
            if (requestFeature == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, requestError ?? "Invalid GeoJSON payload.");
            }

            var payloadFeatureId = OgcFeatureIdentifierResolver.FormatPayloadId(requestFeature.Id);
            if (payloadFeatureId is not null &&
                !string.Equals(payloadFeatureId, expectedFeatureId, StringComparison.Ordinal))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Payload feature ID '{payloadFeatureId}' does not match route feature ID '{featureId}'.");
            }

            var payloadPublicId = OgcFeatureIdentifierResolver.FormatPayloadPublicId(requestFeature.Properties, layer);
            if (payloadPublicId is not null &&
                !string.Equals(payloadPublicId, expectedFeatureId, StringComparison.Ordinal))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Payload public feature ID '{payloadPublicId}' does not match route feature ID '{featureId}'.");
            }
            requestFeature = EnsureFeaturePublicId(requestFeature, layer, expectedFeatureId);

            // Check if feature exists and validate ETag if provided
            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = OgcFeatureEntityTag.Compute(existing, _etagService);
                if (!OgcFeatureEntityTag.MatchesEntityOrRepresentation(ifMatch, etag, _etagService))
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
                var editResult = await ExecuteEditAsync(
                    context,
                    layerId,
                    layer,
                    new OgcFeaturesEditRequest
                    {
                        Operation = OgcFeaturesEditOperation.Replace,
                        Feature = feature,
                        ObjectId = objectId,
                        IfMatch = ifMatch
                    },
                    cancellationToken);
                var updateResult = editResult.UpdateResults.FirstOrDefault();
                if (!updateResult.IsSuccess)
                {
                    if (IsNotFound(updateResult))
                    {
                        Log.ReplaceFeatureNotFound(_logger, collectionId, featureId);
                        return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
                    }

                    return StandardErrorHelpers.CreateInternalServerError(context, updateResult.ErrorMessage ?? "An error occurred while replacing the feature.");
                }

                var updated = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
                if (!updated.HasValue)
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Updated feature could not be reloaded.");
                }

                var responseFeature = await OgcFeaturesResponseHelpers.LoadFeatureForResponseAsync(
                    _featureReader,
                    layerId,
                    layer,
                    objectId,
                    inputCrs,
                    cancellationToken).ConfigureAwait(false);
                if (!responseFeature.HasValue)
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Updated feature response could not be projected.");
                }

                var newETag = OgcFeatureEntityTag.Compute(updated.Value, _etagService);

                context.Response.Headers.ETag = newETag;

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    OgcFeatureIdentifierResolver.FormatPublicId(responseFeature.Value, layer),
                    MediaTypes.GeoJson,
                    inputCrs.Uri);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(responseFeature.Value, layer, inputCrs.AxisOrder, updateLinks);

                await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
                await _mutationEventService.PublishAsync(
                    context,
                    layerId,
                    updated.Value.Id,
                    "update",
                    HonuaTelemetry.Protocols.OgcFeatures,
                    CancellationToken.None,
                    mutationFeature: updated.Value,
                    serviceProtocol: ServiceProtocols.OgcFeatures).ConfigureAwait(false);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.ReplaceFeatureConflict(_logger, collectionId, featureId);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
            catch (ResourceNotFoundException)
            {
                Log.ReplaceFeatureNotFound(_logger, collectionId, featureId);
                HonuaTelemetry.RecordException(Activity.Current, new ResourceNotFoundException($"Feature '{featureId}' not found."));
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException ex)
            {
                Log.ReplaceFeatureFailed(_logger, collectionId, ex);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while replacing the feature.");
            }
            catch (ArgumentException ex)
            {
                Log.ReplaceFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                HonuaTelemetry.RecordException(Activity.Current, ex);
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
            HonuaTelemetry.RecordException(Activity.Current, ex);
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

            var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
                _featureReader,
                _queryProcessor,
                layer,
                featureId,
                cancellationToken).ConfigureAwait(false);
            if (!resolvedFeature.HasValue)
            {
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }

            var objectId = resolvedFeature.Value.ObjectId;

            var existing = resolvedFeature.Value.Feature;
            var expectedFeatureId = OgcFeatureIdentifierResolver.FormatPublicId(existing, layer);

            if (!string.IsNullOrWhiteSpace(ifMatch))
            {
                var etag = OgcFeatureEntityTag.Compute(existing, _etagService);
                if (!OgcFeatureEntityTag.MatchesEntityOrRepresentation(ifMatch, etag, _etagService))
                {
                    return Results.Problem(
                        statusCode: 412,
                        title: "Precondition Failed",
                        detail: "The resource has been modified since the provided ETag.");
                }
            }

            var contentTypeError = OgcFeaturePayloadReader.ValidatePatchContentType(context);
            if (contentTypeError is not null)
            {
                return contentTypeError;
            }

            var (patchRequest, patchError) = await ReadPatchRequestAsync(context, cancellationToken);
            if (patchRequest == null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, patchError ?? "Invalid patch payload.");
            }

            if (patchRequest.FeatureId is not null &&
                !string.Equals(patchRequest.FeatureId, expectedFeatureId, StringComparison.Ordinal))
            {
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    $"Payload feature ID '{patchRequest.FeatureId}' does not match route feature ID '{featureId}'.");
            }

            if (patchRequest.HasProperties)
            {
                var payloadPublicId = OgcFeatureIdentifierResolver.FormatPayloadPublicId(patchRequest.Properties, layer);
                if (payloadPublicId is not null &&
                    !string.Equals(payloadPublicId, expectedFeatureId, StringComparison.Ordinal))
                {
                    return StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"Payload public feature ID '{payloadPublicId}' does not match route feature ID '{featureId}'.");
                }
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

            byte[]? geometryWkb = existing.Geometry;
            if (patchRequest.HasGeometry)
            {
                if (patchRequest.Geometry == null)
                {
                    geometryWkb = null;
                }
                else
                {
                    var wkbResult = await _geometryServices.TryCreateWkbFromGeoJsonAsync(
                        patchRequest.Geometry,
                        inputCrs.Srid,
                        layer.SpatialReference.ToSrid(),
                        inputCrs.AxisOrder,
                        cancellationToken);
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

            var attributesBuilder = existing.Attributes.ToBuilder();
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
                var editResult = await ExecuteEditAsync(
                    context,
                    layerId,
                    layer,
                    new OgcFeaturesEditRequest
                    {
                        Operation = OgcFeaturesEditOperation.Patch,
                        Feature = feature,
                        ObjectId = objectId,
                        IfMatch = ifMatch
                    },
                    cancellationToken);
                var updateResult = editResult.UpdateResults.FirstOrDefault();
                if (!updateResult.IsSuccess)
                {
                    if (IsNotFound(updateResult))
                    {
                        Log.PatchFeatureNotFound(_logger, collectionId, featureId);
                        return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
                    }

                    return StandardErrorHelpers.CreateInternalServerError(context, updateResult.ErrorMessage ?? "An error occurred while patching the feature.");
                }

                var updated = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
                if (!updated.HasValue)
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Updated feature could not be reloaded.");
                }

                var responseFeature = await OgcFeaturesResponseHelpers.LoadFeatureForResponseAsync(
                    _featureReader,
                    layerId,
                    layer,
                    objectId,
                    inputCrs,
                    cancellationToken).ConfigureAwait(false);
                if (!responseFeature.HasValue)
                {
                    return StandardErrorHelpers.CreateInternalServerError(context, "Updated feature response could not be projected.");
                }

                var newETag = OgcFeatureEntityTag.Compute(updated.Value, _etagService);
                context.Response.Headers.ETag = newETag;

                var updateLinks = OgcFeaturesUtilities.BuildFeatureLinks(
                    context.Request,
                    collectionId,
                    OgcFeatureIdentifierResolver.FormatPublicId(responseFeature.Value, layer),
                    MediaTypes.GeoJson,
                    inputCrs.Uri);
                context.Response.Headers["Content-Crs"] = $"<{inputCrs.Uri}>";
                var response = ToOgcFeature(responseFeature.Value, layer, inputCrs.AxisOrder, updateLinks);

                await _mutationEventService.InvalidateLayerAsync(null, layerId, CancellationToken.None);
                await _mutationEventService.PublishAsync(
                    context,
                    layerId,
                    updated.Value.Id,
                    "update",
                    HonuaTelemetry.Protocols.OgcFeatures,
                    CancellationToken.None,
                    mutationFeature: updated.Value,
                    serviceProtocol: ServiceProtocols.OgcFeatures).ConfigureAwait(false);
                HonuaTelemetry.SetSuccess(activity);
                return Results.Json(response, OgcJsonContext.Default.GeoJsonFeature, contentType: MediaTypes.GeoJson);
            }
            catch (ResourceConflictException ex)
            {
                Log.PatchFeatureConflict(_logger, collectionId, featureId);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateFromException(context, ex);
            }
            catch (ResourceNotFoundException)
            {
                Log.PatchFeatureNotFound(_logger, collectionId, featureId);
                HonuaTelemetry.RecordException(Activity.Current, new ResourceNotFoundException($"Feature '{featureId}' not found."));
                return StandardErrorHelpers.CreateNotFound(context, $"Feature '{featureId}' not found.");
            }
            catch (InvalidOperationException ex)
            {
                Log.PatchFeatureFailed(_logger, collectionId, ex);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while patching the feature.");
            }
            catch (ArgumentException ex)
            {
                Log.PatchFeatureInvalidPayload(_logger, collectionId, featureId, ex.Message);
                HonuaTelemetry.RecordException(Activity.Current, ex);
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid feature payload.");
            }
            catch (NotSupportedException ex)
            {
                HonuaTelemetry.RecordException(Activity.Current, ex);
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
            HonuaTelemetry.RecordException(Activity.Current, ex);
            return StandardErrorHelpers.CreateInternalServerError(context, "An error occurred while patching the feature.");
        }
    }

    private async Task<FeatureEditResult> ExecuteEditAsync(
        HttpContext context,
        int layerId,
        LayerDefinition layer,
        OgcFeaturesEditRequest request,
        CancellationToken cancellationToken)
    {
        var editAdapterResult = await _editParameterAdapter.ConvertAsync(request, layer, cancellationToken);
        if (!editAdapterResult.IsSuccess || editAdapterResult.EditRequest == null)
        {
            throw new InvalidOperationException(editAdapterResult.ErrorMessage ?? "Invalid edit request.");
        }

        var optimizedEdit = _editProcessor.OptimizeEdit(editAdapterResult.EditRequest.Value, layer);
        var editValidation = _editProcessor.ValidateEdit(optimizedEdit, layer);
        if (!editValidation.IsValid)
        {
            throw new InvalidOperationException(editValidation.ErrorMessage ?? "Invalid edit request.");
        }

        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedEdit, layer);
        var outboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
            context,
            layerId,
            HonuaTelemetry.Protocols.OgcFeatures,
            serviceProtocol: ServiceProtocols.OgcFeatures,
            layerSrid: layer.SpatialReference.Wkid,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        using var outboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(outboxScopeData);
        return await _featureWriter.ApplyEditsAsync(layerId, editBatch, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsNotFound(EditOperationResult result)
        => !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
           result.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private async Task<PreparedBatchValidationResult> PrepareBatchOperationAsync(
        int layerId,
        LayerDefinition layer,
        BatchOperationModel operation,
        BatchPreparationState state,
        CrsDefinition inputCrs,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(operation.Type))
            {
                return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                    operation.Id,
                    "Batch operation type is required.",
                    400));
            }

            switch (operation.Type.ToUpperInvariant())
            {
                case "CREATE":
                    return await PrepareCreateOperationAsync(layerId, layer, operation, inputCrs, cancellationToken);
                case "UPDATE":
                    return await PrepareUpdateOperationAsync(layer, operation, state, inputCrs, cancellationToken);
                case "DELETE":
                    return await PrepareDeleteOperationAsync(layer, operation, state, cancellationToken);
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
        BatchOperationModel operation,
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
        LayerDefinition layer,
        BatchOperationModel operation,
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

        var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
            _featureReader,
            _queryProcessor,
            layer,
            operation.FeatureId,
            cancellationToken).ConfigureAwait(false);
        if (!resolvedFeature.HasValue)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        var objectId = resolvedFeature.Value.ObjectId;
        var expectedFeatureId = OgcFeatureIdentifierResolver.FormatPublicId(resolvedFeature.Value.Feature, layer);

        if (TryValidateRequestFeaturePublicId(
                operation.Feature,
                layer,
                expectedFeatureId,
                out var identityError))
        {
            operation = operation with
            {
                Feature = EnsureFeaturePublicId(operation.Feature, layer, expectedFeatureId)
            };
        }
        else
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                identityError ?? "Payload feature ID does not match operation feature ID.",
                400));
        }

        if (state.DeletedObjectIds.Contains(objectId))
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

    private static bool TryValidateRequestFeaturePublicId(
        GeoJsonFeature feature,
        LayerDefinition layer,
        string expectedFeatureId,
        out string? error)
    {
        error = null;

        var payloadFeatureId = OgcFeatureIdentifierResolver.FormatPayloadId(feature.Id);
        if (payloadFeatureId is not null &&
            !string.Equals(payloadFeatureId, expectedFeatureId, StringComparison.Ordinal))
        {
            error = $"Payload feature ID '{payloadFeatureId}' does not match operation feature ID '{expectedFeatureId}'.";
            return false;
        }

        var payloadPublicId = OgcFeatureIdentifierResolver.FormatPayloadPublicId(feature.Properties, layer);
        if (payloadPublicId is not null &&
            !string.Equals(payloadPublicId, expectedFeatureId, StringComparison.Ordinal))
        {
            error = $"Payload public feature ID '{payloadPublicId}' does not match operation feature ID '{expectedFeatureId}'.";
            return false;
        }

        return true;
    }

    private static GeoJsonFeature EnsureFeaturePublicId(
        GeoJsonFeature feature,
        LayerDefinition layer,
        string expectedFeatureId)
    {
        var publicIdField = OgcFeatureIdentifierResolver.ResolveWritablePublicIdField(layer);
        if (publicIdField is null ||
            feature.Properties.ContainsKey(publicIdField.Name))
        {
            return feature;
        }

        var properties = new Dictionary<string, object?>(feature.Properties, StringComparer.OrdinalIgnoreCase)
        {
            [publicIdField.Name] = expectedFeatureId
        };
        return feature with { Properties = properties };
    }

    private async Task<PreparedBatchValidationResult> PrepareDeleteOperationAsync(
        LayerDefinition layer,
        BatchOperationModel operation,
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

        var resolvedFeature = await OgcFeatureIdentifierResolver.ResolveAsync(
            _featureReader,
            _queryProcessor,
            layer,
            operation.FeatureId,
            cancellationToken).ConfigureAwait(false);
        if (!resolvedFeature.HasValue)
        {
            return PreparedBatchValidationResult.Failure(CreateBatchFailure(
                operation.Id,
                $"Feature '{operation.FeatureId}' not found.",
                404));
        }

        var objectId = resolvedFeature.Value.ObjectId;

        if (state.DeletedObjectIds.Contains(objectId))
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

    private async Task<List<BatchOperationResult>> MapBatchEditResultsAsync(
        int operationCount,
        int layerId,
        LayerDefinition layer,
        CrsDefinition inputCrs,
        ImmutableArray<PreparedBatchOperation> preparedOperations,
        FeatureEditResult editResult,
        CancellationToken cancellationToken)
    {
        var results = new BatchOperationResult[operationCount];
        var createIndex = 0;
        var updateIndex = 0;
        var deleteIndex = 0;

        foreach (var operation in preparedOperations)
        {
            results[operation.Index] = operation.OperationKind switch
            {
                BatchOperationKind.Create => await MapCreateEditResultAsync(
                    operation.Operation,
                    layerId,
                    layer,
                    inputCrs,
                    GetEditResult(editResult.CreateResults, createIndex++),
                    cancellationToken).ConfigureAwait(false),
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

    private static long?[] MapBatchObjectIdsByOperationIndex(
        int operationCount,
        ImmutableArray<PreparedBatchOperation> preparedOperations,
        FeatureEditResult editResult)
    {
        var objectIds = new long?[operationCount];
        var createIndex = 0;
        var updateIndex = 0;
        var deleteIndex = 0;

        foreach (var operation in preparedOperations)
        {
            switch (operation.OperationKind)
            {
                case BatchOperationKind.Create:
                    objectIds[operation.Index] = GetEditResult(editResult.CreateResults, createIndex++).ObjectId;
                    break;
                case BatchOperationKind.Update:
                    var updateResult = GetEditResult(editResult.UpdateResults, updateIndex++);
                    objectIds[operation.Index] = operation.ObjectId ?? updateResult.ObjectId;
                    break;
                case BatchOperationKind.Delete:
                    var deleteResult = GetEditResult(editResult.DeleteResults, deleteIndex++);
                    objectIds[operation.Index] = operation.ObjectId ?? deleteResult.ObjectId;
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported batch operation kind {operation.OperationKind}.");
            }
        }

        return objectIds;
    }

    private static EditOperationResult GetEditResult(ImmutableArray<EditOperationResult> results, int index)
        => index < results.Length
            ? results[index]
            : EditOperationResult.Failure("Operation result was missing.", errorCode: 500);

    private async Task<BatchOperationResult> MapCreateEditResultAsync(
        BatchOperationModel operation,
        int layerId,
        LayerDefinition layer,
        CrsDefinition inputCrs,
        EditOperationResult editResult,
        CancellationToken cancellationToken)
    {
        if (editResult.IsSuccess && editResult.ObjectId.HasValue)
        {
            var responseFeature = await OgcFeaturesResponseHelpers.LoadFeatureForResponseAsync(
                _featureReader,
                layerId,
                layer,
                editResult.ObjectId.Value,
                inputCrs,
                cancellationToken).ConfigureAwait(false);
            var featureId = responseFeature.HasValue
                ? OgcFeatureIdentifierResolver.FormatPublicId(responseFeature.Value, layer)
                : OgcFeatureIdentifierResolver.FormatPayloadId(operation.Feature?.Id)
                  ?? OgcFeatureIdentifierResolver.FormatPayloadPublicId(operation.Feature?.Properties, layer)
                  ?? editResult.ObjectId.Value.ToString(CultureInfo.InvariantCulture);

            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = featureId,
                StatusCode = 201
            };
        }

        return CreateBatchFailure(operation.Id, editResult.ErrorMessage ?? "Create operation failed.", DetermineBatchFailureStatus(editResult));
    }

    private static BatchOperationResult MapUpdateEditResult(
        BatchOperationModel operation,
        long? objectId,
        EditOperationResult editResult)
    {
        if (editResult.IsSuccess)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = !string.IsNullOrWhiteSpace(operation.FeatureId)
                    ? operation.FeatureId
                    : (editResult.ObjectId ?? objectId ?? 0).ToString(CultureInfo.InvariantCulture),
                StatusCode = 200
            };
        }

        return CreateBatchFailure(operation.Id, editResult.ErrorMessage ?? "Update operation failed.", DetermineBatchFailureStatus(editResult));
    }

    private static BatchOperationResult MapDeleteEditResult(
        BatchOperationModel operation,
        long? objectId,
        EditOperationResult editResult)
    {
        if (editResult.IsSuccess)
        {
            return new BatchOperationResult
            {
                OperationId = operation.Id,
                IsSuccess = true,
                FeatureId = !string.IsNullOrWhiteSpace(operation.FeatureId)
                    ? operation.FeatureId
                    : (objectId ?? editResult.ObjectId ?? 0).ToString(CultureInfo.InvariantCulture),
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
            if (request == null)
            {
                return (null, "Invalid batch request payload.");
            }

            if (request.Operations == null)
            {
                return (null, "Batch request operations are required.");
            }

            for (var i = 0; i < request.Operations.Count; i++)
            {
                if (request.Operations[i] == null)
                {
                    return (null, $"Batch operation at index {i} is required.");
                }
            }

            return (request, null);
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

            string? payloadId = null;
            if (root.TryGetProperty("id", out var idProperty))
            {
                payloadId = idProperty.ValueKind switch
                {
                    JsonValueKind.Number when idProperty.TryGetInt64(out var parsedId) =>
                        parsedId.ToString(CultureInfo.InvariantCulture),
                    JsonValueKind.String => idProperty.GetString(),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(payloadId))
                {
                    return (null, "GeoJSON 'id' must be a string or integer when provided.");
                }
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
        BatchOperationModel operation,
        BatchOperationResult result,
        PreparedBatchOperation? preparedOperation,
        long? operationObjectId,
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
                if (operationObjectId.HasValue)
                {
                    objectId = operationObjectId.Value;
                    eventOperation = "create";
                    return true;
                }

                if (!long.TryParse(result.FeatureId, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId))
                {
                    return false;
                }

                eventOperation = "create";
                return true;
            case "UPDATE":
                if (preparedOperation?.ObjectId is not long updateObjectId)
                {
                    return false;
                }

                objectId = updateObjectId;
                eventOperation = "update";
                return true;
            case "DELETE":
                if (preparedOperation?.ObjectId is not long deleteObjectId)
                {
                    return false;
                }

                objectId = deleteObjectId;
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
        BatchOperationModel Operation,
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
        string? FeatureId,
        bool HasGeometry,
        SimpleGeoJsonGeometry? Geometry,
        bool HasProperties,
        Dictionary<string, object?>? Properties);

    private GeoJsonFeature ToOgcFeature(
        Feature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        ImmutableArray<Link>? links = null)
        => OgcGeoJsonFeatureBuilder.Create(
            feature,
            layer,
            axisOrder,
            _geometryServices,
            links: links);

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

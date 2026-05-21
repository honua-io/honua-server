// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Atomicity-group (change-set) processing for the OData $batch handler.
/// Executes all writes in a change-set as a single transactional unit with rollback on failure.
/// </summary>
internal sealed partial class ODataBatchHandler
{
    private async Task<ImmutableArray<ODataBatchResponseItem>> ProcessAtomicGroupAsync(
        HttpContext context,
        ImmutableArray<ODataBatchRequestItem> requests,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var responses = new List<ODataBatchResponseItem>();
        var rollback = false;

        // Collect all operations for atomic execution
        var createRequests = new Dictionary<int, List<(string requestId, Feature feature, bool geometryChanged)>>();
        var updateRequests = new Dictionary<int, List<(string requestId, long objectId, Feature feature, bool geometryChanged)>>();
        var deleteRequests = new Dictionary<int, List<(string requestId, long objectId, Feature existingFeature)>>();
        var reads = new List<(string requestId, int layerId, long? objectId)>();
        var writeLayerIds = new HashSet<int>();
        var layerCache = new Dictionary<int, LayerDefinition>();

        foreach (var request in requests)
        {
            try
            {
                if (request.Url.TrimStart().StartsWith('$'))
                {
                    responses.Add(CreateErrorResponse(
                        request.Id,
                        400,
                        "InvalidRequest",
                        "Content-ID references are not supported inside atomicity groups."));
                    rollback = true;
                    continue;
                }

                if (!TryNormalizeClientBatchTargetUrl(request.Url, out var normalizedUrl, out var urlError))
                {
                    responses.Add(CreateErrorResponse(
                        request.Id,
                        400,
                        "InvalidRequest",
                        urlError ?? "Invalid batch request URL."));
                    rollback = true;
                    continue;
                }

                var parsed = ParseUrl(normalizedUrl);
                if (parsed.Kind is not ODataResourceKind.Feature && parsed.Kind is not ODataResourceKind.Features)
                {
                    responses.Add(CreateErrorResponse(
                        request.Id,
                        400,
                        "InvalidRequest",
                        $"Unsupported OData resource '{request.Url}'."));
                    rollback = true;
                    continue;
                }

                if (parsed.Tail != ODataPathTailKind.None &&
                    !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    responses.Add(CreateErrorResponse(
                        request.Id,
                        405,
                        "MethodNotAllowed",
                        "Only GET is supported for $ref and $value requests."));
                    rollback = true;
                    continue;
                }

                var layerId = parsed.LayerId;
                if (!layerId.HasValue && request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && request.Body != null)
                {
                    if (!ODataFeaturePayloadParser.TryParse(request.Body, out var payload, out var payloadError))
                    {
                        responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", payloadError ?? "Invalid request body."));
                        rollback = true;
                        continue;
                    }

                    if (payload.LayerId.HasValue)
                    {
                        layerId = payload.LayerId;
                    }
                }

                if (!layerId.HasValue)
                {
                    responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", "LayerId is required for feature operations."));
                    rollback = true;
                    continue;
                }
                var objectId = parsed.ObjectId;
                if (!layerCache.TryGetValue(layerId.Value, out var layer))
                {
                    layer = await _layerCatalog.GetLayerAsync(layerId.Value, cancellationToken);
                    if (layer == null)
                    {
                        responses.Add(CreateErrorResponse(
                            request.Id,
                            404,
                            "ResourceNotFound",
                            $"Layer {layerId} not found."));
                        rollback = true;
                        continue;
                    }

                    layerCache[layerId.Value] = layer;
                }

                var service = await LayerValidationHelpers.ResolvePrimaryServiceAsync(
                    context,
                    layerId.Value,
                    ServiceProtocols.OData,
                    cancellationToken);
                if (!IsODataEnabled(layer, service))
                {
                    responses.Add(CreateErrorResponse(
                        request.Id,
                        404,
                        "ResourceNotFound",
                        "OData is not enabled for this service."));
                    rollback = true;
                    continue;
                }

                var accessError = await ValidateBatchRequestAccessAsync(
                    context,
                    request.Id,
                    layer,
                    service,
                    request.Method,
                    cancellationToken);
                if (accessError != null)
                {
                    responses.Add(accessError);
                    rollback = true;
                    continue;
                }

                switch (request.Method.ToUpperInvariant())
                {
                    case "GET":
                        rollback = true;
                        responses.Add(CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            "Atomicity groups do not support GET requests."));
                        break;

                    case "POST":
                        {
                            if (request.Body == null)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    "Request body is required for POST operations."));
                                rollback = true;
                                break;
                            }

                            Feature parsedFeature;
                            // Parse the body once to capture the request-intent geometry flag
                            // before CreateFeatureFromBodyAsync re-parses internally; the flag
                            // tracks whether the originating body specified a Geometry property
                            // (rather than the post-merge feature's WKB), so the outbox payload's
                            // GeometryChanged matches the inline publish path's contract.
                            var requestGeometryChanged = TryGetGeometrySpecified(request.Body);
                            try
                            {
                                parsedFeature = await CreateFeatureFromBodyAsync(request.Body, layer, cancellationToken: cancellationToken);
                            }
                            catch (ArgumentException)
                            {
                                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", "Invalid request data."));
                                rollback = true;
                                break;
                            }

                            var createValidation = await BuildValidatedEditBatchAsync(
                                layer,
                                CreateEditRequestFromFeature(ODataOperation.Create, parsedFeature),
                                rollbackOnFailure: true,
                                cancellationToken);
                            if (createValidation.ErrorMessage != null ||
                                createValidation.Batch == null ||
                                createValidation.Batch.Value.Creates.IsDefaultOrEmpty)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    createValidation.ErrorMessage ?? "Invalid request data."));
                                rollback = true;
                                break;
                            }

                            var feature = createValidation.Batch.Value.Creates[0];
                            if (!createRequests.TryGetValue(layerId.Value, out var createList))
                            {
                                createList = new List<(string requestId, Feature feature, bool geometryChanged)>();
                                createRequests[layerId.Value] = createList;
                            }

                            createList.Add((request.Id, feature, requestGeometryChanged));
                            writeLayerIds.Add(layerId.Value);
                            break;
                        }

                    case "PATCH":
                    case "PUT":
                        {
                            var isPatch = request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
                            if (!objectId.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    "ObjectId is required for update operations."));
                                rollback = true;
                                break;
                            }

                            if (request.Body == null)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    "Request body is required for update operations."));
                                rollback = true;
                                break;
                            }

                            var existing = await _featureReader.GetAsync(layerId.Value, objectId.Value, cancellationToken);
                            if (!existing.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    404,
                                    "ResourceNotFound",
                                    $"Feature {objectId} not found in layer {layerId}."));
                                rollback = true;
                                break;
                            }

                            var precondition = await ValidatePreconditionsAsync(layer, existing.Value, request.Headers, cancellationToken);
                            if (!precondition.IsValid)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    412,
                                    "PreconditionFailed",
                                    precondition.ErrorMessage ?? "Precondition failed."));
                                rollback = true;
                                break;
                            }

                            Feature? mergeExisting = isPatch ? existing.Value : null;

                            Feature parsedFeature;
                            // PATCH preserves existing geometry when the body omits Geometry,
                            // so the request-intent flag from the body is the right signal.
                            // PUT (replace) re-sets geometry to whatever the body carries (or
                            // null if absent); a body-less PUT therefore clears any existing
                            // geometry, so flag the change when the request body specified
                            // Geometry OR the existing row already had geometry that PUT will
                            // overwrite. Reading the merged feature's WKB would over-report
                            // PATCH attribute-only updates because the merge preserves the
                            // prior WKB.
                            var requestGeometryChanged = TryGetGeometrySpecified(request.Body)
                                || (!isPatch && existing.Value.Geometry is { Length: > 0 });
                            try
                            {
                                parsedFeature = await CreateFeatureFromBodyAsync(
                                    request.Body,
                                    layer,
                                    objectId.Value,
                                    mergeExisting,
                                    cancellationToken: cancellationToken);
                            }
                            catch (ArgumentException)
                            {
                                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", "Invalid request data."));
                                rollback = true;
                                break;
                            }

                            var updateValidation = await BuildValidatedEditBatchAsync(
                                layer,
                                CreateEditRequestFromFeature(
                                    isPatch ? ODataOperation.Patch : ODataOperation.Update,
                                    parsedFeature,
                                    objectId.Value,
                                    request.Headers?.GetValueOrDefault("If-Match"),
                                    request.Headers?.GetValueOrDefault("If-None-Match")),
                                rollbackOnFailure: true,
                                cancellationToken);
                            if (updateValidation.ErrorMessage != null ||
                                updateValidation.Batch == null ||
                                updateValidation.Batch.Value.Updates.IsDefaultOrEmpty)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    updateValidation.ErrorMessage ?? "Invalid request data."));
                                rollback = true;
                                break;
                            }

                            var feature = updateValidation.Batch.Value.Updates[0];
                            if (!updateRequests.TryGetValue(layerId.Value, out var updateList))
                            {
                                updateList = new List<(string requestId, long objectId, Feature feature, bool geometryChanged)>();
                                updateRequests[layerId.Value] = updateList;
                            }

                            updateList.Add((request.Id, objectId.Value, feature, requestGeometryChanged));
                            writeLayerIds.Add(layerId.Value);
                            break;
                        }

                    case "DELETE":
                        {
                            if (!objectId.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    "ObjectId is required for delete operations."));
                                rollback = true;
                                break;
                            }

                            var existing = await _featureReader.GetAsync(layerId.Value, objectId.Value, cancellationToken);
                            if (!existing.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    404,
                                    "ResourceNotFound",
                                    $"Feature {objectId} not found in layer {layerId}."));
                                rollback = true;
                                break;
                            }

                            var precondition = await ValidatePreconditionsAsync(layer, existing.Value, request.Headers, cancellationToken);
                            if (!precondition.IsValid)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    412,
                                    "PreconditionFailed",
                                    precondition.ErrorMessage ?? "Precondition failed."));
                                rollback = true;
                                break;
                            }

                            var deleteValidation = await BuildValidatedEditBatchAsync(
                                layer,
                                new ODataEditRequest
                                {
                                    Operation = ODataOperation.Delete,
                                    ObjectId = objectId.Value,
                                    IfMatch = request.Headers?.GetValueOrDefault("If-Match"),
                                    IfNoneMatch = request.Headers?.GetValueOrDefault("If-None-Match"),
                                    Payload = new ParsedFeaturePayload()
                                },
                                rollbackOnFailure: true,
                                cancellationToken);
                            if (deleteValidation.ErrorMessage != null ||
                                deleteValidation.Batch == null ||
                                deleteValidation.Batch.Value.Deletes.IsDefaultOrEmpty)
                            {
                                responses.Add(CreateErrorResponse(
                                    request.Id,
                                    400,
                                    "InvalidRequest",
                                    deleteValidation.ErrorMessage ?? "Invalid request data."));
                                rollback = true;
                                break;
                            }

                            if (!deleteRequests.TryGetValue(layerId.Value, out var deleteList))
                            {
                                deleteList = new List<(string requestId, long objectId, Feature existingFeature)>();
                                deleteRequests[layerId.Value] = deleteList;
                            }

                            deleteList.Add((request.Id, deleteValidation.Batch.Value.Deletes[0], existing.Value));
                            writeLayerIds.Add(layerId.Value);
                            break;
                        }

                    default:
                        responses.Add(CreateErrorResponse(
                            request.Id,
                            405,
                            "MethodNotAllowed",
                            $"Method {request.Method} is not supported."));
                        rollback = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.BatchRequestParseFailed(_logger, request.Id, ex);
                rollback = true;
                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", "Failed to parse request."));
            }
        }

        if (rollback)
        {
            // Mark all unprocessed as failed due to atomicity
            foreach (var request in requests.Where(r => !responses.Any(resp => resp.Id == r.Id)))
            {
                responses.Add(CreateErrorResponse(
                    request.Id,
                    424,
                    "DependencyFailed",
                    "Request failed due to atomicity group failure."));
            }
            return responses.ToImmutableArray();
        }

        if (writeLayerIds.Count > 1)
        {
            foreach (var request in requests.Where(r => !responses.Any(resp => resp.Id == r.Id)))
            {
                responses.Add(CreateErrorResponse(
                    request.Id,
                    400,
                    "InvalidRequest",
                    "Atomicity groups with write operations must target a single layer."));
            }

            return responses.ToImmutableArray();
        }

        var addCount = createRequests.Values.Sum(list => list.Count);
        var updateCount = updateRequests.Values.Sum(list => list.Count);
        var deleteCount = deleteRequests.Values.Sum(list => list.Count);
        var totalCount = addCount + updateCount + deleteCount;

        if (addCount > _editLimits.MaxFeaturesPerEdit ||
            updateCount > _editLimits.MaxFeaturesPerEdit ||
            deleteCount > _editLimits.MaxFeaturesPerEdit)
        {
            var message = $"Too many features in a single edit operation. Maximum per operation: {_editLimits.MaxFeaturesPerEdit}.";
            foreach (var request in requests.Where(r => !responses.Any(resp => resp.Id == r.Id)))
            {
                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", message));
            }

            return responses.ToImmutableArray();
        }

        if (totalCount > _editLimits.MaxEditsPerTransaction)
        {
            var message = $"Too many edits in a single request. Maximum per request: {_editLimits.MaxEditsPerTransaction}.";
            foreach (var request in requests.Where(r => !responses.Any(resp => resp.Id == r.Id)))
            {
                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", message));
            }

            return responses.ToImmutableArray();
        }

        // Process reads first (they don't need transactions)
        foreach (var (requestId, layerId, objectId) in reads)
        {
            try
            {
                if (!layerCache.TryGetValue(layerId, out var layer))
                {
                    layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
                    if (layer != null)
                    {
                        layerCache[layerId] = layer;
                    }
                }

                if (layer == null)
                {
                    responses.Add(CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Layer {layerId} not found."));
                    continue;
                }

                if (objectId.HasValue)
                {
                    var feature = await _featureReader.GetAsync(layerId, objectId.Value, cancellationToken);
                    if (feature.HasValue)
                    {
                        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
                        var payload = FeatureToBody(feature.Value, layer, axisOrder, baseUrl, out var etag);
                        responses.Add(CreateSuccessResponse(
                            requestId,
                            200,
                            payload,
                            new Dictionary<string, string> { ["ETag"] = etag }));
                    }
                    else
                    {
                        responses.Add(CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found."));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.BatchReadFailed(_logger, requestId, ex);
                // Use safe error message to avoid leaking internal details
                responses.Add(CreateErrorResponse(requestId, 500, "InternalError", "An error occurred while reading the feature."));
            }
        }

        // Process writes in a batch with rollback on failure
        try
        {
            var layerIds = new HashSet<int>(createRequests.Keys);
            layerIds.UnionWith(updateRequests.Keys);
            layerIds.UnionWith(deleteRequests.Keys);

            foreach (var layerId in layerIds)
            {
                if (!layerCache.TryGetValue(layerId, out var layer))
                {
                    layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
                    if (layer != null)
                    {
                        layerCache[layerId] = layer;
                    }
                }

                if (layer == null)
                {
                    continue;
                }

                var batch = new FeatureEditBatch { RollbackOnFailure = true };

                createRequests.TryGetValue(layerId, out var layerCreates);
                updateRequests.TryGetValue(layerId, out var layerUpdates);
                deleteRequests.TryGetValue(layerId, out var layerDeletes);

                if (layerCreates is { Count: > 0 })
                {
                    batch = batch with { Creates = layerCreates.Select(item => item.feature).ToImmutableArray() };
                }

                if (layerUpdates is { Count: > 0 })
                {
                    batch = batch with { Updates = layerUpdates.Select(item => item.feature).ToImmutableArray() };
                }

                if (layerDeletes is { Count: > 0 })
                {
                    batch = batch with { Deletes = layerDeletes.Select(item => item.objectId).ToImmutableArray() };
                }

                if (batch.IsEmpty)
                {
                    continue;
                }

                var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
                // Preserve per-subrequest correlation and protocol-level geometry-change
                // intent in the outbox payload: ApplyEditsAsync invokes the entry factory
                // once per row in creates-then-updates-then-deletes order, matching the
                // order we capture from layerCreates/Updates/Deletes here. Deletes default
                // to false for geometryChanged.
                Dictionary<string, IReadOnlyList<string>>? perOperationRequestIds = null;
                Dictionary<string, IReadOnlyList<bool>>? perOperationGeometryChanged = null;
                if ((layerCreates?.Count ?? 0) + (layerUpdates?.Count ?? 0) + (layerDeletes?.Count ?? 0) > 0)
                {
                    perOperationRequestIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                    perOperationGeometryChanged = new Dictionary<string, IReadOnlyList<bool>>(StringComparer.Ordinal);
                    if (layerCreates is { Count: > 0 })
                    {
                        perOperationRequestIds["create"] = layerCreates
                            .Select(item => $"{context.TraceIdentifier}:{item.requestId}")
                            .ToImmutableArray();
                        perOperationGeometryChanged["create"] = layerCreates
                            .Select(static item => item.geometryChanged)
                            .ToImmutableArray();
                    }
                    if (layerUpdates is { Count: > 0 })
                    {
                        perOperationRequestIds["update"] = layerUpdates
                            .Select(item => $"{context.TraceIdentifier}:{item.requestId}")
                            .ToImmutableArray();
                        // Use the per-row request-intent flag captured before
                        // CreateFeatureFromBodyAsync merged with the existing row;
                        // attribute-only PATCH on a spatial feature would otherwise
                        // be reported as a geometry change.
                        perOperationGeometryChanged["update"] = layerUpdates
                            .Select(static item => item.geometryChanged)
                            .ToImmutableArray();
                    }
                    if (layerDeletes is { Count: > 0 })
                    {
                        perOperationRequestIds["delete"] = layerDeletes
                            .Select(item => $"{context.TraceIdentifier}:{item.requestId}")
                            .ToImmutableArray();
                        perOperationGeometryChanged["delete"] = Enumerable
                            .Repeat(false, layerDeletes.Count)
                            .ToImmutableArray();
                    }
                }

                var atomicOutboxScopeData = await _mutationEventService.ResolveOutboxScopeAsync(
                    context,
                    layerId,
                    HonuaTelemetry.Protocols.OData,
                    serviceProtocol: ServiceProtocols.OData,
                    // Match the inline path: ToSrid() returns LatestWkid ?? Wkid so
                    // the outbox enrichment fallback emits the latest SRID for layers
                    // like Wkid=102100/LatestWkid=3857.
                    layerSrid: layer.SpatialReference.ToSrid(),
                    perOperationRequestIds: perOperationRequestIds,
                    perOperationGeometryChanged: perOperationGeometryChanged,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                using var atomicOutboxScope = Honua.Core.Features.Infrastructure.Events.Outbox.FeatureMutationOutboxScope.BeginIfNotNull(atomicOutboxScopeData);
                var result = await _featureWriter.ApplyEditsAsync(layerId, batch, cancellationToken);

                if (layerCreates != null)
                {
                    for (var i = 0; i < result.CreateResults.Length && i < layerCreates.Count; i++)
                    {
                        var createResult = result.CreateResults[i];
                        var (requestId, requestedFeature, _) = layerCreates[i];

                        if (createResult.IsSuccess && createResult.ObjectId.HasValue)
                        {
                            var createdFeature = await _featureReader.GetAsync(layerId, createResult.ObjectId.Value, cancellationToken).ConfigureAwait(false);
                            if (!createdFeature.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    requestId,
                                    500,
                                    "CreateReadbackFailed",
                                    $"Created feature {createResult.ObjectId.Value} could not be reloaded."));
                                continue;
                            }

                            var payload = FeatureToBody(createdFeature.Value, layer, axisOrder, baseUrl, out var etag);

                            var headers = new Dictionary<string, string>
                            {
                                ["Location"] = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createResult.ObjectId})",
                                ["OData-EntityId"] = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createResult.ObjectId})",
                                ["EntityId"] = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createResult.ObjectId})",
                                ["ETag"] = etag
                            };

                            responses.Add(CreateSuccessResponse(
                                requestId,
                                201,
                                payload,
                                headers));
                        }
                        else
                        {
                            responses.Add(CreateErrorResponse(requestId, 400, "CreateFailed", createResult.ErrorMessage ?? "Create operation failed."));
                        }
                    }
                }

                if (layerUpdates != null)
                {
                    for (var i = 0; i < result.UpdateResults.Length && i < layerUpdates.Count; i++)
                    {
                        var updateResult = result.UpdateResults[i];
                        var (requestId, _, updatedFeature, _) = layerUpdates[i];

                        if (updateResult.IsSuccess)
                        {
                            var persistedObjectId = updateResult.ObjectId ?? updatedFeature.Id;
                            var persistedFeature = await _featureReader.GetAsync(layerId, persistedObjectId, cancellationToken).ConfigureAwait(false);
                            if (!persistedFeature.HasValue)
                            {
                                responses.Add(CreateErrorResponse(
                                    requestId,
                                    500,
                                    "UpdateReadbackFailed",
                                    $"Updated feature {persistedObjectId} could not be reloaded."));
                                continue;
                            }

                            var payload = FeatureToBody(persistedFeature.Value, layer, axisOrder, baseUrl, out var etag);
                            var headers = new Dictionary<string, string> { ["ETag"] = etag };

                            responses.Add(CreateSuccessResponse(
                                requestId,
                                200,
                                payload,
                                headers));
                        }
                        else
                        {
                            responses.Add(CreateErrorResponse(requestId, 400, "UpdateFailed", updateResult.ErrorMessage ?? "Update operation failed."));
                        }
                    }
                }

                if (layerDeletes != null)
                {
                    for (var i = 0; i < result.DeleteResults.Length && i < layerDeletes.Count; i++)
                    {
                        var deleteResult = result.DeleteResults[i];
                        var (requestId, _, existingFeature) = layerDeletes[i];

                        if (deleteResult.IsSuccess)
                        {
                            responses.Add(CreateSuccessResponse(
                                requestId,
                                204,
                                null,
                                mutationFeature: existingFeature));
                        }
                        else
                        {
                            responses.Add(CreateErrorResponse(requestId, 400, "DeleteFailed", deleteResult.ErrorMessage ?? "Delete operation failed."));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.BatchAtomicGroupFailed(_logger, ex);

            // Mark all write operations as failed
            foreach (var createList in createRequests.Values)
            {
                foreach (var (requestId, _, _) in createList.Where(c => !responses.Any(r => r.Id == c.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }

            foreach (var updateList in updateRequests.Values)
            {
                foreach (var (requestId, _, _, _) in updateList.Where(u => !responses.Any(r => r.Id == u.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }

            foreach (var deleteList in deleteRequests.Values)
            {
                foreach (var (requestId, _, _) in deleteList.Where(d => !responses.Any(r => r.Id == d.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }
        }

        return responses.ToImmutableArray();
    }
}

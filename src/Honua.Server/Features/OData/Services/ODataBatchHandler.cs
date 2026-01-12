// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.OData.Models;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Log category for OData batch operations.
/// </summary>
internal sealed class ODataBatchLog;

/// <summary>
/// Handles OData v4 $batch operations with support for atomicity groups.
/// </summary>
internal sealed partial class ODataBatchHandler
{
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGeometryService _geometryService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly FeatureMutationValidator _mutationValidator;
    private readonly EditLimits _editLimits;
    private readonly IETagService _etagService;
    private readonly ILogger _logger;
    private readonly Dictionary<int, AxisOrder> _axisOrderCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataBatchHandler"/> class.
    /// </summary>
    public ODataBatchHandler(
        ODataBatchDependencies dependencies,
        IETagService etagService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        _layerCatalog = dependencies.LayerCatalog;
        _featureReader = dependencies.FeatureReader;
        _featureWriter = dependencies.FeatureWriter;
        _geometryService = dependencies.GeometryService;
        _crsRegistry = dependencies.CrsRegistry;
        _mutationValidator = dependencies.MutationValidator;
        _editLimits = dependencies.EditLimits;
        _etagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes a batch request and returns the aggregated response.
    /// </summary>
    public async Task<ODataBatchResponse> ProcessBatchAsync(
        ODataBatchRequest batchRequest,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var responses = new List<ODataBatchResponseItem>();

        // Group requests by atomicity group
        var atomicGroups = batchRequest.Requests
            .GroupBy(r => r.AtomicityGroup ?? Guid.NewGuid().ToString())
            .ToList();

        foreach (var group in atomicGroups)
        {
            var isAtomicGroup = group.First().AtomicityGroup != null;

            if (isAtomicGroup)
            {
                // Process atomic group - all succeed or all fail
                var groupResponses = await ProcessAtomicGroupAsync(
                    group.ToImmutableArray(),
                    baseUrl,
                    cancellationToken);
                responses.AddRange(groupResponses);
            }
            else
            {
                // Process individual requests
                foreach (var request in group)
                {
                    var response = await ProcessSingleRequestAsync(
                        request,
                        baseUrl,
                        cancellationToken);
                    responses.Add(response);
                }
            }
        }

        return new ODataBatchResponse
        {
            Responses = responses.ToArray()
        };
    }

    private async Task<ImmutableArray<ODataBatchResponseItem>> ProcessAtomicGroupAsync(
        ImmutableArray<ODataBatchRequestItem> requests,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var responses = new List<ODataBatchResponseItem>();
        var rollback = false;

        // Collect all operations for atomic execution
        var createRequests = new Dictionary<int, List<(string requestId, Feature feature)>>();
        var updateRequests = new Dictionary<int, List<(string requestId, long objectId, Feature feature)>>();
        var deleteRequests = new Dictionary<int, List<(string requestId, long objectId)>>();
        var reads = new List<(string requestId, int layerId, long? objectId)>();
        var writeLayerIds = new HashSet<int>();
        var layerCache = new Dictionary<int, LayerDefinition>();

        foreach (var request in requests)
        {
            try
            {
                var parsed = ParseUrl(request.Url);
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
                        if (request.Body != null)
                        {
                            var feature = await CreateFeatureFromBodyAsync(request.Body, layer, cancellationToken: cancellationToken);
                            if (!createRequests.TryGetValue(layerId.Value, out var createList))
                            {
                                createList = new List<(string requestId, Feature feature)>();
                                createRequests[layerId.Value] = createList;
                            }

                            createList.Add((request.Id, feature));
                            writeLayerIds.Add(layerId.Value);
                        }
                        break;

                    case "PATCH":
                    case "PUT":
                        if (objectId.HasValue && request.Body != null)
                        {
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

                            Feature? mergeExisting = request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase)
                                ? existing.Value
                                : null;

                            var feature = await CreateFeatureFromBodyAsync(
                                request.Body,
                                layer,
                                objectId.Value,
                                mergeExisting,
                                cancellationToken: cancellationToken);
                            if (!updateRequests.TryGetValue(layerId.Value, out var updateList))
                            {
                                updateList = new List<(string requestId, long objectId, Feature feature)>();
                                updateRequests[layerId.Value] = updateList;
                            }

                            updateList.Add((request.Id, objectId.Value, feature));
                            writeLayerIds.Add(layerId.Value);
                        }
                        break;

                    case "DELETE":
                        if (objectId.HasValue)
                        {
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

                            if (!deleteRequests.TryGetValue(layerId.Value, out var deleteList))
                            {
                                deleteList = new List<(string requestId, long objectId)>();
                                deleteRequests[layerId.Value] = deleteList;
                            }

                            deleteList.Add((request.Id, objectId.Value));
                            writeLayerIds.Add(layerId.Value);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.BatchRequestParseFailed(_logger, request.Id, ex);
                rollback = true;
                var message = ex is ArgumentException ? ex.Message : "Failed to parse request.";
                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", message));
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
                var result = await _featureWriter.ApplyEditsAsync(layerId, batch, cancellationToken);

                if (layerCreates != null)
                {
                    for (var i = 0; i < result.CreateResults.Length && i < layerCreates.Count; i++)
                    {
                        var createResult = result.CreateResults[i];
                        var (requestId, _) = layerCreates[i];

                        if (createResult.IsSuccess && createResult.ObjectId.HasValue)
                        {
                            var createdFeature = await _featureReader.GetAsync(layerId, createResult.ObjectId.Value, cancellationToken);
                            Dictionary<string, object?>? payload = null;
                            string? etag = null;
                            if (createdFeature.HasValue)
                            {
                                payload = FeatureToBody(createdFeature.Value, layer, axisOrder, baseUrl, out var computedEtag);
                                etag = computedEtag;
                            }

                            var headers = new Dictionary<string, string>
                            {
                                ["Location"] = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createResult.ObjectId})",
                                ["OData-EntityId"] = $"{baseUrl}/odata/Features(LayerId={layerId},ObjectId={createResult.ObjectId})"
                            };
                            if (!string.IsNullOrWhiteSpace(etag))
                            {
                                headers["ETag"] = etag;
                            }

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
                        var (requestId, objectId, _) = layerUpdates[i];

                        if (updateResult.IsSuccess)
                        {
                            var updatedFeature = await _featureReader.GetAsync(layerId, objectId, cancellationToken);
                            Dictionary<string, object?>? payload = null;
                            string? etag = null;
                            if (updatedFeature.HasValue)
                            {
                                payload = FeatureToBody(updatedFeature.Value, layer, axisOrder, baseUrl, out var computedEtag);
                                etag = computedEtag;
                            }

                            Dictionary<string, string>? headers = null;
                            if (!string.IsNullOrWhiteSpace(etag))
                            {
                                headers = new Dictionary<string, string> { ["ETag"] = etag };
                            }

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
                        var (requestId, _) = layerDeletes[i];

                        if (deleteResult.IsSuccess)
                        {
                            responses.Add(CreateSuccessResponse(requestId, 204, null));
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
                foreach (var (requestId, _) in createList.Where(c => !responses.Any(r => r.Id == c.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }

            foreach (var updateList in updateRequests.Values)
            {
                foreach (var (requestId, _, _) in updateList.Where(u => !responses.Any(r => r.Id == u.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }

            foreach (var deleteList in deleteRequests.Values)
            {
                foreach (var (requestId, _) in deleteList.Where(d => !responses.Any(r => r.Id == d.requestId)))
                {
                    responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
                }
            }
        }

        return responses.ToImmutableArray();
    }

    private async Task<ODataBatchResponseItem> ProcessSingleRequestAsync(
        ODataBatchRequestItem request,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsed = ParseUrl(request.Url);
            var layerId = parsed.LayerId;
            var objectId = parsed.ObjectId;

            if (parsed.Kind == ODataResourceKind.Layer)
            {
                var layerDefinition = layerId.HasValue
                    ? await _layerCatalog.GetLayerAsync(layerId.Value, cancellationToken)
                    : null;
                if (layerDefinition == null)
                {
                    return CreateErrorResponse(request.Id, 404, "ResourceNotFound", $"Layer {layerId} not found.");
                }

                if (!request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    return CreateErrorResponse(request.Id, 405, "MethodNotAllowed", "Only GET is supported for layers in batch.");
                }

                var layerPayload = ODataUtilityService.BuildLayerPayload(layerDefinition);
                layerPayload["@odata.context"] = ODataUtilityService.BuildContextUrl(baseUrl, "Layers", isSingle: true);
                return CreateSuccessResponse(request.Id, 200, layerPayload);
            }

            if (parsed.Kind is not ODataResourceKind.Feature && parsed.Kind is not ODataResourceKind.Features)
            {
                return CreateErrorResponse(request.Id, 400, "InvalidRequest", $"Unsupported OData resource '{request.Url}'.");
            }

            // Verify layer exists
            if (!layerId.HasValue && request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && request.Body != null)
            {
                if (ODataFeaturePayloadParser.TryParse(request.Body, out var payload, out _))
                {
                    layerId = payload.LayerId;
                }
            }

            if (!layerId.HasValue)
            {
                return CreateErrorResponse(request.Id, 400, "InvalidRequest", "LayerId is required for feature requests.");
            }

            var layer = await _layerCatalog.GetLayerAsync(layerId.Value, cancellationToken);
            if (layer == null)
            {
                return CreateErrorResponse(request.Id, 404, "ResourceNotFound", $"Layer {layerId} not found.");
            }

            switch (request.Method.ToUpperInvariant())
            {
                case "GET":
                    return await HandleGetAsync(request.Id, layer, objectId, baseUrl, cancellationToken);

                case "POST":
                    return await HandlePostAsync(request.Id, layer, request.Body, baseUrl, cancellationToken);

                case "PATCH":
                case "PUT":
                    return await HandlePatchAsync(request.Id, layer, objectId, request.Body, request.Headers, baseUrl, cancellationToken);

                case "DELETE":
                    return await HandleDeleteAsync(request.Id, layerId.Value, objectId, request.Headers, cancellationToken);

                default:
                    return CreateErrorResponse(request.Id, 405, "MethodNotAllowed", $"Method {request.Method} is not supported.");
            }
        }
        catch (ArgumentException ex)
        {
            Log.BatchRequestParseFailed(_logger, request.Id, ex);
            return CreateErrorResponse(request.Id, 400, "InvalidRequest", ex.Message);
        }
        catch (Exception ex)
        {
            Log.BatchSingleRequestFailed(_logger, request.Id, ex);
            // Use safe error message to avoid leaking internal details
            return CreateErrorResponse(request.Id, 500, "InternalError", "An error occurred while processing the request.");
        }
    }

    private async Task<ODataBatchResponseItem> HandleGetAsync(
        string requestId,
        LayerDefinition layer,
        long? objectId,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            // Collection query not supported in batch
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Collection queries are not supported in batch. Specify an object ID.");
        }

        var feature = await _featureReader.GetAsync(layer.Id, objectId.Value, cancellationToken);
        if (!feature.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layer.Id}.");
        }

        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var payload = FeatureToBody(feature.Value, layer, axisOrder, baseUrl, out var etag);
        return CreateSuccessResponse(
            requestId,
            200,
            payload,
            new Dictionary<string, string> { ["ETag"] = etag });
    }

    private async Task<ODataBatchResponseItem> HandlePostAsync(
        string requestId,
        LayerDefinition layer,
        Dictionary<string, object?>? body,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (body == null)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Request body is required for POST.");
        }

        Feature feature;
        try
        {
            feature = await CreateFeatureFromBodyAsync(body, layer, cancellationToken: cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", ex.Message);
        }

        var created = await _featureWriter.CreateAsync(layer.Id, feature, cancellationToken);
        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var payload = FeatureToBody(created, layer, axisOrder, baseUrl, out var etag);

        return CreateSuccessResponse(
            requestId,
            201,
            payload,
            new Dictionary<string, string>
            {
                ["Location"] = $"{baseUrl}/odata/Features(LayerId={layer.Id},ObjectId={created.Id})",
                ["OData-EntityId"] = $"{baseUrl}/odata/Features(LayerId={layer.Id},ObjectId={created.Id})",
                ["ETag"] = etag
            });
    }

    private async Task<ODataBatchResponseItem> HandlePatchAsync(
        string requestId,
        LayerDefinition layer,
        long? objectId,
        Dictionary<string, object?>? body,
        Dictionary<string, string>? headers,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Object ID is required for PATCH.");
        }

        if (body == null)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Request body is required for PATCH.");
        }

        // Get existing feature
        var existing = await _featureReader.GetAsync(layer.Id, objectId.Value, cancellationToken);
        if (!existing.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layer.Id}.");
        }

        var precondition = await ValidatePreconditionsAsync(layer, existing.Value, headers, cancellationToken);
        if (!precondition.IsValid)
        {
            return CreateErrorResponse(requestId, 412, "PreconditionFailed", precondition.ErrorMessage ?? "Precondition failed.");
        }

        // Merge with updates
        Feature updatedFeature;
        try
        {
            updatedFeature = await CreateFeatureFromBodyAsync(body, layer, objectId.Value, existing.Value, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", ex.Message);
        }

        var result = await _featureWriter.UpdateAsync(layer.Id, updatedFeature, cancellationToken);

        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var updatedPayload = FeatureToBody(result, layer, axisOrder, baseUrl, out var etag);
        return CreateSuccessResponse(
            requestId,
            200,
            updatedPayload,
            new Dictionary<string, string> { ["ETag"] = etag });
    }

    private async Task<ODataBatchResponseItem> HandleDeleteAsync(
        string requestId,
        int layerId,
        long? objectId,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Object ID is required for DELETE.");
        }

        var existing = await _featureReader.GetAsync(layerId, objectId.Value, cancellationToken);
        if (!existing.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}.");
        }

        var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
        if (layer == null)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Layer {layerId} not found.");
        }

        var precondition = await ValidatePreconditionsAsync(layer, existing.Value, headers, cancellationToken);
        if (!precondition.IsValid)
        {
            return CreateErrorResponse(requestId, 412, "PreconditionFailed", precondition.ErrorMessage ?? "Precondition failed.");
        }

        var deleted = await _featureWriter.DeleteAsync(layerId, objectId.Value, cancellationToken);
        if (!deleted)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}.");
        }

        return CreateSuccessResponse(requestId, 204, null);
    }

    private static ODataParsedPath ParseUrl(string url)
    {
        if (!ODataPathParser.TryParse(url, out var parsed, out var errorMessage))
        {
            throw new ArgumentException(errorMessage ?? "Invalid OData URL.");
        }

        return parsed;
    }

    private async Task<Feature> CreateFeatureFromBodyAsync(
        Dictionary<string, object?> body,
        LayerDefinition layer,
        long? objectId = null,
        Feature? existing = null,
        CancellationToken cancellationToken = default)
    {
        if (!ODataFeaturePayloadParser.TryParse(body, out var payload, out var payloadError))
        {
            throw new ArgumentException(payloadError ?? "Invalid request body.");
        }

        if (payload.LayerId.HasValue && payload.LayerId.Value != layer.Id)
        {
            throw new ArgumentException("LayerId in payload does not match request URL.");
        }

        byte[]? geometry = existing.HasValue ? existing.Value.Geometry : null;
        if (payload.Geometry != null)
        {
            var crsResolution = await ODataCrsUtilities.TryResolveGeometryCrsAsync(
                _crsRegistry,
                payload.Geometry,
                layer.SpatialReference.ToSrid(),
                cancellationToken);
            if (!crsResolution.IsValid)
            {
                throw new ArgumentException(crsResolution.ErrorMessage ?? "Unsupported geometry CRS.");
            }

            var conversion = ODataGeometryConverter.ConvertGeometryToWkb(
                _geometryService,
                payload.Geometry,
                layer.SpatialReference.ToSrid(),
                crsResolution.Definition);

            if (!conversion.IsSuccess)
            {
                throw new ArgumentException(conversion.ErrorMessage ?? "Invalid geometry.");
            }

            geometry = conversion.Wkb;

            var geometryValidation = await _mutationValidator.ValidateGeometryAsync(geometry, cancellationToken);
            if (!geometryValidation.IsValid)
            {
                throw new ArgumentException($"Invalid geometry: {geometryValidation.ErrorMessage}");
            }

            geometry = geometryValidation.Geometry;
        }

        var attributes = existing.HasValue
            ? new Dictionary<string, object?>(existing.Value.Attributes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (payload.Attributes.Count > 0)
        {
            var attributesResult = _mutationValidator.ValidateAttributes(
                layer,
                payload.Attributes,
                ValidationExtensions.AttributeValidationMode.Strict);
            if (!attributesResult.IsValid)
            {
                throw new ArgumentException(attributesResult.ErrorMessage ?? "Invalid attributes.");
            }

            foreach (var kvp in attributesResult.Value!)
            {
                attributes[kvp.Key] = kvp.Value;
            }
        }

        return Feature.Create(
            objectId ?? 0,
            geometry,
            attributes.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private async ValueTask<AxisOrder> ResolveAxisOrderAsync(LayerDefinition layer, CancellationToken cancellationToken)
    {
        if (_axisOrderCache.TryGetValue(layer.Id, out var axisOrder))
        {
            return axisOrder;
        }

        axisOrder = await ODataCrsUtilities.ResolveAxisOrderAsync(
            _crsRegistry,
            layer.SpatialReference.ToSrid(),
            cancellationToken);
        _axisOrderCache[layer.Id] = axisOrder;
        return axisOrder;
    }

    private Dictionary<string, object?> FeatureToBody(
        Feature feature,
        LayerDefinition layer,
        AxisOrder axisOrder,
        string baseUrl,
        out string etag)
    {
        var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
            _geometryService,
            feature.Geometry,
            layer.SpatialReference.ToSrid(),
            axisOrder);
        var attributes = ODataAttributeSerializer.Serialize(feature.Attributes);
        var payload = ODataUtilityService.BuildFeaturePayload(layer.Id, feature, geometry, attributes);
        etag = ComputeFeatureEtag(payload);
        payload["@odata.etag"] = etag;
        payload["@odata.context"] = ODataUtilityService.BuildContextUrl(baseUrl, "Features", isSingle: true);
        return payload;
    }

    private static ODataBatchResponseItem CreateSuccessResponse(
        string id,
        int status,
        object? body,
        Dictionary<string, string>? headers = null)
    {
        return new ODataBatchResponseItem
        {
            Id = id,
            Status = status,
            Headers = headers,
            Body = body
        };
    }

    private static ODataBatchResponseItem CreateErrorResponse(string id, int status, string code, string message)
    {
        return new ODataBatchResponseItem
        {
            Id = id,
            Status = status,
            Body = new ODataError
            {
                Error = new ErrorDetails
                {
                    Code = code,
                    Message = message
                }
            }
        };
    }

    private string ComputeFeatureEtag(Dictionary<string, object?> payload)
    {
        var canonical = ODataUtilityService.NormalizeForEtag(payload);
        var json = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return _etagService.ComputeETag(json);
    }

    private async Task<(bool IsValid, string? ErrorMessage)> ValidatePreconditionsAsync(
        LayerDefinition layer,
        Feature feature,
        Dictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        var ifMatch = GetHeaderValue(headers, "If-Match");
        var ifNoneMatch = GetHeaderValue(headers, "If-None-Match");

        if (string.IsNullOrWhiteSpace(ifMatch) && string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return (true, null);
        }

        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var geometry = ODataGeometryConverter.ConvertWkbToGeometry(
            _geometryService,
            feature.Geometry,
            layer.SpatialReference.ToSrid(),
            axisOrder);
        var attributes = ODataAttributeSerializer.Serialize(feature.Attributes);
        var payload = ODataUtilityService.BuildFeaturePayload(layer.Id, feature, geometry, attributes);
        var etag = ComputeFeatureEtag(payload);

        if (!string.IsNullOrWhiteSpace(ifMatch) &&
            !_etagService.MatchesPrecondition(ifMatch, etag))
        {
            return (false, "ETag does not match the current resource.");
        }

        if (!string.IsNullOrWhiteSpace(ifNoneMatch) &&
            !_etagService.IsModified(ifNoneMatch, etag))
        {
            return (false, "Resource has not changed.");
        }

        return (true, null);
    }

    private static string? GetHeaderValue(Dictionary<string, string>? headers, string name)
    {
        if (headers == null)
        {
            return null;
        }

        foreach (var kvp in headers)
        {
            if (kvp.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static partial class Log
    {
        /// <summary>
        /// Logs when an OData batch request fails to parse.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="requestId">The identifier of the batch request that failed to parse.</param>
        /// <param name="exception">The exception that caused the parsing failure.</param>
        [LoggerMessage(EventId = 3100, Level = LogLevel.Warning, Message = "Batch request {RequestId} failed to parse.")]
        public static partial void BatchRequestParseFailed(ILogger logger, string requestId, Exception exception);

        /// <summary>
        /// Logs when a batch read request fails during processing.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="requestId">The identifier of the batch request that failed to read.</param>
        /// <param name="exception">The exception that caused the read failure.</param>
        [LoggerMessage(EventId = 3101, Level = LogLevel.Warning, Message = "Batch read request {RequestId} failed.")]
        public static partial void BatchReadFailed(ILogger logger, string requestId, Exception exception);

        /// <summary>
        /// Logs when an atomic batch group fails, requiring all operations in the group to be rolled back.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="exception">The exception that caused the atomic group failure.</param>
        [LoggerMessage(EventId = 3102, Level = LogLevel.Error, Message = "Batch atomic group failed.")]
        public static partial void BatchAtomicGroupFailed(ILogger logger, Exception exception);

        /// <summary>
        /// Logs when a single request within a batch operation fails.
        /// </summary>
        /// <param name="logger">The logger instance.</param>
        /// <param name="requestId">The identifier of the single request that failed.</param>
        /// <param name="exception">The exception that caused the request failure.</param>
        [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "Batch single request {RequestId} failed.")]
        public static partial void BatchSingleRequestFailed(ILogger logger, string requestId, Exception exception);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Server.Features.Infrastructure.Security;
using Honua.Server.Features.Infrastructure.Validation;
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
    private readonly IFeatureStore _featureStore;
    private readonly EditLimits _editLimits;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataBatchHandler"/> class.
    /// </summary>
    public ODataBatchHandler(
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        EditLimits editLimits,
        ILogger logger)
    {
        _layerCatalog = layerCatalog;
        _featureStore = featureStore;
        _editLimits = editLimits;
        _logger = logger;
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
                var (layerId, objectId) = ParseUrl(request.Url);
                if (!layerCache.TryGetValue(layerId, out var layer))
                {
                    layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
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

                    layerCache[layerId] = layer;
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
                            var feature = CreateFeatureFromBody(request.Body, layer);
                            if (!createRequests.TryGetValue(layerId, out var createList))
                            {
                                createList = new List<(string requestId, Feature feature)>();
                                createRequests[layerId] = createList;
                            }

                            createList.Add((request.Id, feature));
                            writeLayerIds.Add(layerId);
                        }
                        break;

                    case "PATCH":
                    case "PUT":
                        if (objectId.HasValue && request.Body != null)
                        {
                            var feature = CreateFeatureFromBody(request.Body, layer, objectId.Value);
                            if (!updateRequests.TryGetValue(layerId, out var updateList))
                            {
                                updateList = new List<(string requestId, long objectId, Feature feature)>();
                                updateRequests[layerId] = updateList;
                            }

                            updateList.Add((request.Id, objectId.Value, feature));
                            writeLayerIds.Add(layerId);
                        }
                        break;

                    case "DELETE":
                        if (objectId.HasValue)
                        {
                            if (!deleteRequests.TryGetValue(layerId, out var deleteList))
                            {
                                deleteList = new List<(string requestId, long objectId)>();
                                deleteRequests[layerId] = deleteList;
                            }

                            deleteList.Add((request.Id, objectId.Value));
                            writeLayerIds.Add(layerId);
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
                if (objectId.HasValue)
                {
                    var feature = await _featureStore.GetAsync(layerId, objectId.Value, cancellationToken);
                    if (feature.HasValue)
                    {
                        responses.Add(CreateSuccessResponse(requestId, 200, FeatureToBody(feature.Value, layerId)));
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

                var result = await _featureStore.ApplyEditsAsync(layerId, batch, cancellationToken);

                if (layerCreates != null)
                {
                    for (var i = 0; i < result.CreateResults.Length && i < layerCreates.Count; i++)
                    {
                        var createResult = result.CreateResults[i];
                        var (requestId, _) = layerCreates[i];

                        if (createResult.IsSuccess && createResult.ObjectId.HasValue)
                        {
                            var createdFeature = await _featureStore.GetAsync(layerId, createResult.ObjectId.Value, cancellationToken);
                            responses.Add(CreateSuccessResponse(
                                requestId,
                                201,
                                createdFeature.HasValue ? FeatureToBody(createdFeature.Value, layerId) : null,
                                new Dictionary<string, string>
                                {
                                    ["Location"] = $"{baseUrl}/odata/Features({layerId},{createResult.ObjectId})",
                                    ["OData-EntityId"] = $"{baseUrl}/odata/Features({layerId},{createResult.ObjectId})"
                                }));
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
                            var updatedFeature = await _featureStore.GetAsync(layerId, objectId, cancellationToken);
                            responses.Add(CreateSuccessResponse(
                                requestId,
                                200,
                                updatedFeature.HasValue ? FeatureToBody(updatedFeature.Value, layerId) : null));
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
            var (layerId, objectId) = ParseUrl(request.Url);

            // Verify layer exists
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer == null)
            {
                return CreateErrorResponse(request.Id, 404, "ResourceNotFound", $"Layer {layerId} not found.");
            }

            switch (request.Method.ToUpperInvariant())
            {
                case "GET":
                    return await HandleGetAsync(request.Id, layerId, objectId, cancellationToken);

                case "POST":
                    return await HandlePostAsync(request.Id, layer, request.Body, baseUrl, cancellationToken);

                case "PATCH":
                case "PUT":
                    return await HandlePatchAsync(request.Id, layer, objectId, request.Body, cancellationToken);

                case "DELETE":
                    return await HandleDeleteAsync(request.Id, layerId, objectId, cancellationToken);

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
        int layerId,
        long? objectId,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            // Collection query not supported in batch
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Collection queries are not supported in batch. Specify an object ID.");
        }

        var feature = await _featureStore.GetAsync(layerId, objectId.Value, cancellationToken);
        if (!feature.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}.");
        }

        return CreateSuccessResponse(requestId, 200, FeatureToBody(feature.Value, layerId));
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
            feature = CreateFeatureFromBody(body, layer);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", ex.Message);
        }

        var created = await _featureStore.CreateAsync(layer.Id, feature, cancellationToken);

        return CreateSuccessResponse(
            requestId,
            201,
            FeatureToBody(created, layer.Id),
            new Dictionary<string, string>
            {
                ["Location"] = $"{baseUrl}/odata/Features({layer.Id},{created.Id})",
                ["OData-EntityId"] = $"{baseUrl}/odata/Features({layer.Id},{created.Id})"
            });
    }

    private async Task<ODataBatchResponseItem> HandlePatchAsync(
        string requestId,
        LayerDefinition layer,
        long? objectId,
        Dictionary<string, object?>? body,
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
        var existing = await _featureStore.GetAsync(layer.Id, objectId.Value, cancellationToken);
        if (!existing.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layer.Id}.");
        }

        // Merge with updates
        Feature updatedFeature;
        try
        {
            updatedFeature = CreateFeatureFromBody(body, layer, objectId.Value, existing.Value);
        }
        catch (ArgumentException ex)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", ex.Message);
        }

        var result = await _featureStore.UpdateAsync(layer.Id, updatedFeature, cancellationToken);

        return CreateSuccessResponse(requestId, 200, FeatureToBody(result, layer.Id));
    }

    private async Task<ODataBatchResponseItem> HandleDeleteAsync(
        string requestId,
        int layerId,
        long? objectId,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Object ID is required for DELETE.");
        }

        var deleted = await _featureStore.DeleteAsync(layerId, objectId.Value, cancellationToken);
        if (!deleted)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}.");
        }

        return CreateSuccessResponse(requestId, 204, null);
    }

    private static (int layerId, long? objectId) ParseUrl(string url)
    {
        // Parse URLs like "Features(1)" or "Features(1,100)"
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Request URL is required.", nameof(url));
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            url,
            @"^Features\((\d+)(?:,(\d+))?\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            throw new ArgumentException($"Invalid URL format: {url}. Expected format: Features(layerId) or Features(layerId,objectId)");
        }

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var layerId))
        {
            throw new ArgumentException($"Layer ID '{match.Groups[1].Value}' is not a valid integer.");
        }

        long? objectId = null;
        if (match.Groups[2].Success)
        {
            if (!long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedObjectId))
            {
                throw new ArgumentException($"Object ID '{match.Groups[2].Value}' is not a valid integer.");
            }

            objectId = parsedObjectId;
        }

        return (layerId, objectId);
    }

    private static Feature CreateFeatureFromBody(
        Dictionary<string, object?> body,
        LayerDefinition layer,
        long? objectId = null,
        Feature? existing = null)
    {
        byte[]? geometry = existing.HasValue ? existing.Value.Geometry : null;
        if (body.TryGetValue("Geometry", out var geomValue))
        {
            var geometryBase64 = geomValue switch
            {
                null => null,
                string geomString => geomString,
                JsonElement geomElement when geomElement.ValueKind == JsonValueKind.String => geomElement.GetString(),
                JsonElement geomElement when geomElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => throw new ArgumentException("Geometry must be a Base64-encoded WKB string.")
            };

            if (!string.IsNullOrWhiteSpace(geometryBase64))
            {
                try
                {
                    geometry = Convert.FromBase64String(geometryBase64);
                }
                catch (FormatException)
                {
                    throw new ArgumentException("Geometry must be a valid Base64-encoded WKB string.");
                }

                var validationResult = WkbValidation.Validate(geometry);
                if (!validationResult.IsValid)
                {
                    throw new ArgumentException($"Invalid geometry: {validationResult.ErrorMessage}");
                }
            }
            else if (!existing.HasValue)
            {
                geometry = null;
            }
        }

        var attributes = existing.HasValue
            ? new Dictionary<string, object?>(existing.Value.Attributes, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (body.TryGetValue("Attributes", out var attrsValue))
        {
            var parsedAttributes = attrsValue switch
            {
                null => throw new ArgumentException("Attributes must be an object."),
                string attrsString => ParseAttributesJson(attrsString),
                JsonElement attrsElement when attrsElement.ValueKind == JsonValueKind.String => ParseAttributesJson(attrsElement.GetString()),
                JsonElement attrsElement when attrsElement.ValueKind == JsonValueKind.Object => ParseAttributesObject(attrsElement),
                JsonElement attrsElement when attrsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined =>
                    throw new ArgumentException("Attributes must be an object."),
                Dictionary<string, object?> dictAttrs => new Dictionary<string, object?>(dictAttrs, StringComparer.OrdinalIgnoreCase),
                IReadOnlyDictionary<string, object?> dictAttrs => new Dictionary<string, object?>(dictAttrs, StringComparer.OrdinalIgnoreCase),
                _ => throw new ArgumentException("Attributes must be an object or JSON string.")
            };

            var attributesResult = layer.ValidateAttributes(
                parsedAttributes,
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

        static Dictionary<string, object?> ParseAttributesJson(string? attrsString)
        {
            if (string.IsNullOrWhiteSpace(attrsString))
            {
                throw new ArgumentException("Attributes must be a valid JSON object.");
            }

            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    attrsString,
                    ODataJsonContext.Default.DictionaryStringObject);

                return parsed ?? throw new ArgumentException("Attributes must be a valid JSON object.");
            }
            catch (JsonException)
            {
                throw new ArgumentException("Attributes must be a valid JSON object.");
            }
        }

        static Dictionary<string, object?> ParseAttributesObject(JsonElement attrsElement)
        {
            var parsed = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in attrsElement.EnumerateObject())
            {
                parsed[prop.Name] = prop.Value;
            }

            return parsed;
        }
    }

    private static Dictionary<string, object?> FeatureToBody(Feature feature, int layerId)
    {
        return new Dictionary<string, object?>
        {
            ["ObjectId"] = feature.Id,
            ["LayerId"] = layerId,
            ["Geometry"] = feature.Geometry != null ? Convert.ToBase64String(feature.Geometry) : null,
            ["Attributes"] = JsonSerializer.Serialize(
                feature.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                ODataJsonContext.Default.DictionaryStringObject)
        };
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

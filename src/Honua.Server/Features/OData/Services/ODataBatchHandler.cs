// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
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
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ODataBatchHandler"/> class.
    /// </summary>
    public ODataBatchHandler(
        ILayerCatalog layerCatalog,
        IFeatureStore featureStore,
        ILogger logger)
    {
        _layerCatalog = layerCatalog;
        _featureStore = featureStore;
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
        var creates = new List<(string requestId, int layerId, Feature feature)>();
        var updates = new List<(string requestId, int layerId, long objectId, Feature feature)>();
        var deletes = new List<(string requestId, int layerId, long objectId)>();
        var reads = new List<(string requestId, int layerId, long? objectId)>();

        foreach (var request in requests)
        {
            try
            {
                var (layerId, objectId) = ParseUrl(request.Url);

                switch (request.Method.ToUpperInvariant())
                {
                    case "GET":
                        reads.Add((request.Id, layerId, objectId));
                        break;

                    case "POST":
                        if (request.Body != null)
                        {
                            var feature = CreateFeatureFromBody(request.Body);
                            creates.Add((request.Id, layerId, feature));
                        }
                        break;

                    case "PATCH":
                    case "PUT":
                        if (objectId.HasValue && request.Body != null)
                        {
                            var feature = CreateFeatureFromBody(request.Body, objectId.Value);
                            updates.Add((request.Id, layerId, objectId.Value, feature));
                        }
                        break;

                    case "DELETE":
                        if (objectId.HasValue)
                        {
                            deletes.Add((request.Id, layerId, objectId.Value));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.BatchRequestParseFailed(_logger, request.Id, ex);
                rollback = true;
                responses.Add(CreateErrorResponse(request.Id, 400, "InvalidRequest", ex.Message));
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
                responses.Add(CreateErrorResponse(requestId, 500, "InternalError", ex.Message));
            }
        }

        // Process writes in a batch with rollback on failure
        try
        {
            // Build the batch
            var layerBatches = new Dictionary<int, FeatureEditBatch>();

            foreach (var (requestId, layerId, feature) in creates)
            {
                if (!layerBatches.TryGetValue(layerId, out var batch))
                {
                    batch = new FeatureEditBatch { RollbackOnFailure = true };
                    layerBatches[layerId] = batch;
                }
                layerBatches[layerId] = batch with
                {
                    Creates = batch.Creates.Add(feature)
                };
            }

            foreach (var (requestId, layerId, objectId, feature) in updates)
            {
                if (!layerBatches.TryGetValue(layerId, out var batch))
                {
                    batch = new FeatureEditBatch { RollbackOnFailure = true };
                    layerBatches[layerId] = batch;
                }
                layerBatches[layerId] = batch with
                {
                    Updates = batch.Updates.Add(feature)
                };
            }

            foreach (var (requestId, layerId, objectId) in deletes)
            {
                if (!layerBatches.TryGetValue(layerId, out var batch))
                {
                    batch = new FeatureEditBatch { RollbackOnFailure = true };
                    layerBatches[layerId] = batch;
                }
                layerBatches[layerId] = batch with
                {
                    Deletes = batch.Deletes.Add(objectId)
                };
            }

            // Execute batches per layer
            var createIndex = 0;
            var updateIndex = 0;
            var deleteIndex = 0;

            foreach (var (layerId, batch) in layerBatches)
            {
                var result = await _featureStore.ApplyEditsAsync(layerId, batch, cancellationToken);

                // Map results back to request IDs
                foreach (var createResult in result.CreateResults)
                {
                    if (createIndex < creates.Count)
                    {
                        var (requestId, _, _) = creates[createIndex++];
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

                foreach (var updateResult in result.UpdateResults)
                {
                    if (updateIndex < updates.Count)
                    {
                        var (requestId, _, objectId, _) = updates[updateIndex++];
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

                foreach (var deleteResult in result.DeleteResults)
                {
                    if (deleteIndex < deletes.Count)
                    {
                        var (requestId, _, _) = deletes[deleteIndex++];
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
            foreach (var (requestId, _, _) in creates.Where(c => !responses.Any(r => r.Id == c.requestId)))
            {
                responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
            }
            foreach (var (requestId, _, _, _) in updates.Where(u => !responses.Any(r => r.Id == u.requestId)))
            {
                responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
            }
            foreach (var (requestId, _, _) in deletes.Where(d => !responses.Any(r => r.Id == d.requestId)))
            {
                responses.Add(CreateErrorResponse(requestId, 500, "TransactionFailed", "Atomic group transaction failed."));
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
                    return await HandlePostAsync(request.Id, layerId, request.Body, baseUrl, cancellationToken);

                case "PATCH":
                case "PUT":
                    return await HandlePatchAsync(request.Id, layerId, objectId, request.Body, cancellationToken);

                case "DELETE":
                    return await HandleDeleteAsync(request.Id, layerId, objectId, cancellationToken);

                default:
                    return CreateErrorResponse(request.Id, 405, "MethodNotAllowed", $"Method {request.Method} is not supported.");
            }
        }
        catch (Exception ex)
        {
            Log.BatchSingleRequestFailed(_logger, request.Id, ex);
            return CreateErrorResponse(request.Id, 500, "InternalError", ex.Message);
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
        int layerId,
        Dictionary<string, object?>? body,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (body == null)
        {
            return CreateErrorResponse(requestId, 400, "InvalidRequest", "Request body is required for POST.");
        }

        var feature = CreateFeatureFromBody(body);
        var created = await _featureStore.CreateAsync(layerId, feature, cancellationToken);

        return CreateSuccessResponse(
            requestId,
            201,
            FeatureToBody(created, layerId),
            new Dictionary<string, string>
            {
                ["Location"] = $"{baseUrl}/odata/Features({layerId},{created.Id})",
                ["OData-EntityId"] = $"{baseUrl}/odata/Features({layerId},{created.Id})"
            });
    }

    private async Task<ODataBatchResponseItem> HandlePatchAsync(
        string requestId,
        int layerId,
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
        var existing = await _featureStore.GetAsync(layerId, objectId.Value, cancellationToken);
        if (!existing.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layerId}.");
        }

        // Merge with updates
        var updatedFeature = CreateFeatureFromBody(body, objectId.Value, existing.Value);
        var result = await _featureStore.UpdateAsync(layerId, updatedFeature, cancellationToken);

        return CreateSuccessResponse(requestId, 200, FeatureToBody(result, layerId));
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
        var match = System.Text.RegularExpressions.Regex.Match(
            url,
            @"Features\((\d+)(?:,(\d+))?\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            throw new ArgumentException($"Invalid URL format: {url}. Expected format: Features(layerId) or Features(layerId,objectId)");
        }

        var layerId = int.Parse(match.Groups[1].Value);
        long? objectId = match.Groups[2].Success ? long.Parse(match.Groups[2].Value) : null;

        return (layerId, objectId);
    }

    private static Feature CreateFeatureFromBody(Dictionary<string, object?> body, long? objectId = null, Feature? existing = null)
    {
        byte[]? geometry = null;
        var attributes = existing?.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            ?? new Dictionary<string, object?>();

        if (body.TryGetValue("Geometry", out var geomValue) && geomValue is string geomString && !string.IsNullOrEmpty(geomString))
        {
            geometry = Convert.FromBase64String(geomString);
        }
        else if (existing.HasValue)
        {
            geometry = existing.Value.Geometry;
        }

        if (body.TryGetValue("Attributes", out var attrsValue))
        {
            if (attrsValue is string attrsString && !string.IsNullOrEmpty(attrsString))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(attrsString, ODataJsonContext.Default.DictionaryStringObject);
                if (parsed != null)
                {
                    foreach (var kvp in parsed)
                    {
                        attributes[kvp.Key] = kvp.Value;
                    }
                }
            }
            else if (attrsValue is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in jsonElement.EnumerateObject())
                {
                    attributes[prop.Name] = ConvertJsonElement(prop.Value);
                }
            }
            else if (attrsValue is Dictionary<string, object?> dictAttrs)
            {
                foreach (var kvp in dictAttrs)
                {
                    attributes[kvp.Key] = kvp.Value;
                }
            }
        }

        return Feature.Create(objectId ?? 0, geometry, attributes.ToImmutableDictionary());
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longVal) ? longVal :
                element.TryGetDouble(out var doubleVal) ? doubleVal :
                element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(prop => prop.Name, prop => ConvertJsonElement(prop.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToArray(),
            _ => element.GetRawText()
        };
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

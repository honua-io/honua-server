// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Features.Protocols.OData.Services;

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
    private readonly IEditParameterAdapter<ODataEditRequest> _editParameterAdapter;
    private readonly IEditProcessor _editProcessor;
    private readonly Honua.Server.Features.Infrastructure.Events.FeatureMutationEventService _mutationEventService;
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
        _editParameterAdapter = dependencies.EditParameterAdapter;
        _editProcessor = dependencies.EditProcessor;
        _mutationEventService = dependencies.MutationEventService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes a batch request and returns the aggregated response.
    /// </summary>
    public async Task<ODataBatchResponse> ProcessBatchAsync(
        HttpContext context,
        ODataBatchRequest batchRequest,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var responses = new List<ODataBatchResponseItem>();

        // Group requests by atomicity group while preserving first-seen ordering.
        var groupOrder = new List<string>();
        var groupMap = new Dictionary<string, List<ODataBatchRequestItem>>(StringComparer.Ordinal);

        foreach (var request in batchRequest.Requests)
        {
            var groupKey = request.AtomicityGroup == null
                ? NonAtomicGroupKey
                : $"{AtomicGroupPrefix}{request.AtomicityGroup}";

            if (!groupMap.TryGetValue(groupKey, out var groupList))
            {
                groupList = new List<ODataBatchRequestItem>();
                groupMap[groupKey] = groupList;
                groupOrder.Add(groupKey);
            }

            groupList.Add(request);
        }

        foreach (var groupKey in groupOrder)
        {
            var groupRequests = groupMap[groupKey].ToImmutableArray();
            var isAtomicGroup = groupKey.StartsWith(AtomicGroupPrefix, StringComparison.Ordinal);
            var responsesById = new Dictionary<string, ODataBatchResponseItem>(StringComparer.Ordinal);

            if (isAtomicGroup)
            {
                if (HasDependencies(groupRequests))
                {
                    responses.AddRange(CreateAtomicDependencyErrors(groupRequests));
                    continue;
                }

                // Process atomic group - all succeed or all fail
                var groupResponses = await ProcessAtomicGroupAsync(
                    context,
                    groupRequests,
                    baseUrl,
                    cancellationToken);
                responses.AddRange(groupResponses);
                continue;
            }

            var dependencyOrder = OrderRequestsByDependencies(groupRequests);
            if (dependencyOrder.ErrorsById.Count > 0)
            {
                var addedErrors = new HashSet<string>(StringComparer.Ordinal);
                foreach (var request in groupRequests)
                {
                    if (dependencyOrder.ErrorsById.TryGetValue(request.Id, out var error) &&
                        addedErrors.Add(request.Id))
                    {
                        responses.Add(error);
                        responsesById[request.Id] = error;
                    }
                }
            }

            // Process individual requests in dependency order
            foreach (var request in dependencyOrder.OrderedRequests)
            {
                if (request.DependsOn is { Length: > 0 })
                {
                    var failedDependency = request.DependsOn.FirstOrDefault(dep =>
                        responsesById.TryGetValue(dep, out var dependencyResponse) &&
                        dependencyResponse.Status >= 400);

                    if (!string.IsNullOrWhiteSpace(failedDependency))
                    {
                        var dependencyFailed = CreateErrorResponse(
                            request.Id,
                            424,
                            "DependencyFailed",
                            $"Request depends on failed request id '{failedDependency}'.");
                        responses.Add(dependencyFailed);
                        responsesById[request.Id] = dependencyFailed;
                        continue;
                    }
                }

                if (!TryResolveContentIdReferences(request, responsesById, out var resolvedRequest, out var resolutionError))
                {
                    var failedResolution = CreateErrorResponse(
                        request.Id,
                        400,
                        "InvalidRequest",
                        resolutionError ?? "Invalid Content-ID reference.");
                    responses.Add(failedResolution);
                    responsesById[request.Id] = failedResolution;
                    continue;
                }

                var response = await ProcessSingleRequestAsync(
                    context,
                    resolvedRequest,
                    baseUrl,
                    cancellationToken);
                responses.Add(response);
                responsesById[request.Id] = response;
            }
        }

        return new ODataBatchResponse
        {
            Responses = responses.ToArray()
        };
    }

    private const string NonAtomicGroupKey = "__non-atomic__";
    private const string AtomicGroupPrefix = "atomic:";
    private const int DefaultBatchCollectionTop = 1000;
    private const string AbsoluteBatchRequestUrlMessage = "Absolute batch request URLs are not supported.";

    private static bool HasDependencies(ImmutableArray<ODataBatchRequestItem> requests)
        => requests.Any(r => r.DependsOn is { Length: > 0 });

    private static List<ODataBatchResponseItem> CreateAtomicDependencyErrors(
        ImmutableArray<ODataBatchRequestItem> requests)
    {
        const string message = "dependsOn is not supported for atomicity groups.";
        return requests.Select(r => CreateErrorResponse(r.Id, 400, "InvalidRequest", message)).ToList();
    }

    private static DependencyOrderResult OrderRequestsByDependencies(ImmutableArray<ODataBatchRequestItem> requests)
    {
        var errorsById = new Dictionary<string, ODataBatchResponseItem>(StringComparer.Ordinal);
        var requestById = new Dictionary<string, ODataBatchRequestItem>(StringComparer.Ordinal);
        var orderedIds = new List<string>(requests.Length);
        var invalid = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            if (!requestById.TryAdd(request.Id, request))
            {
                errorsById[request.Id] = CreateErrorResponse(
                    request.Id,
                    400,
                    "InvalidRequest",
                    $"Duplicate request id '{request.Id}'.");
                invalid.Add(request.Id);
                continue;
            }

            orderedIds.Add(request.Id);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var request in requests)
            {
                if (invalid.Contains(request.Id) || errorsById.ContainsKey(request.Id))
                {
                    continue;
                }

                if (request.DependsOn == null || request.DependsOn.Length == 0)
                {
                    continue;
                }

                foreach (var dependencyId in request.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            "dependsOn contains an empty request id.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (string.Equals(dependencyId, request.Id, StringComparison.Ordinal))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            "dependsOn cannot reference the same request id.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (!requestById.ContainsKey(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            $"dependsOn references unknown request id '{dependencyId}'.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (invalid.Contains(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            $"dependsOn references invalid request id '{dependencyId}'.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        var validIds = orderedIds
            .Where(id => !invalid.Contains(id) && !errorsById.ContainsKey(id))
            .ToList();
        var validSet = new HashSet<string>(validIds, StringComparer.Ordinal);

        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in validIds)
        {
            indegree[id] = 0;
            dependents[id] = [];
        }

        foreach (var request in requests)
        {
            if (!validSet.Contains(request.Id))
            {
                continue;
            }

            if (request.DependsOn == null || request.DependsOn.Length == 0)
            {
                continue;
            }

            foreach (var dependencyId in request.DependsOn)
            {
                if (!validSet.Contains(dependencyId))
                {
                    continue;
                }

                indegree[request.Id]++;
                dependents[dependencyId].Add(request.Id);
            }
        }

        var queue = new Queue<string>(validIds.Where(id => indegree[id] == 0));
        var orderedRequests = new List<ODataBatchRequestItem>(validIds.Count);
        var processed = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            processed.Add(id);
            orderedRequests.Add(requestById[id]);

            foreach (var dependent in dependents[id])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (processed.Count != validSet.Count)
        {
            foreach (var id in validSet.Where(id => !processed.Contains(id)))
            {
                errorsById[id] = CreateErrorResponse(
                    id,
                    400,
                    "InvalidRequest",
                    "dependsOn contains a cyclic dependency.");
            }
        }

        return new DependencyOrderResult(orderedRequests.ToImmutableArray(), errorsById);
    }

    private sealed record DependencyOrderResult(
        ImmutableArray<ODataBatchRequestItem> OrderedRequests,
        Dictionary<string, ODataBatchResponseItem> ErrorsById);

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
                    layerSrid: layer.SpatialReference.Wkid,
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

    private async Task<ODataBatchResponseItem> ProcessSingleRequestAsync(
        HttpContext context,
        ODataBatchRequestItem request,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            var parsed = ParseUrl(NormalizeRequestUrlForParsing(request.Url));
            var batchContext = CreateBatchRequestContext(context, request, cancellationToken);
            var query = batchContext.Request.Query;
            var queryHandler = batchContext.RequestServices.GetRequiredService<ODataQueryHandler>();
            var streamingHandler = batchContext.RequestServices.GetRequiredService<ODataStreamingQueryHandler>();
            var crudHandler = batchContext.RequestServices.GetRequiredService<ODataCrudHandler>();

            IResult result = parsed.Kind switch
            {
                ODataResourceKind.Layers when request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) =>
                    await queryHandler.HandleGetLayersAsync(
                        batchContext,
                        GetQueryValue(query, "$filter"),
                        GetQueryValue(query, "$select"),
                        GetQueryValue(query, "$orderby"),
                        GetQueryValue(query, "$top"),
                        GetQueryValue(query, "$skip"),
                        GetQueryValue(query, "$skiptoken"),
                        GetQueryValue(query, "$count"),
                        GetQueryValue(query, "$format"),
                        cancellationToken),

                ODataResourceKind.Layer when request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && parsed.LayerId.HasValue =>
                    await queryHandler.HandleGetLayerAsync(
                        batchContext,
                        parsed.LayerId.Value,
                        GetQueryValue(query, "$select"),
                        GetQueryValue(query, "$format"),
                        cancellationToken),

                ODataResourceKind.Features when request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) =>
                    await streamingHandler.HandleGetFeaturesAsync(
                        batchContext,
                        parsed.LayerId,
                        GetQueryValue(query, "$filter"),
                        GetQueryValue(query, "$select"),
                        GetQueryValue(query, "$orderby"),
                        GetQueryValue(query, "$top"),
                        GetQueryValue(query, "$skip"),
                        GetQueryValue(query, "$skiptoken"),
                        GetQueryValue(query, "$count"),
                        GetQueryValue(query, "$expand"),
                        GetQueryValue(query, "$compute"),
                        GetQueryValue(query, "$search"),
                        GetQueryValue(query, "$apply"),
                        GetQueryValue(query, "$deltatoken"),
                        GetQueryValue(query, "$format"),
                        cancellationToken),

                ODataResourceKind.Features when request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) =>
                    await crudHandler.HandleCreateFeatureAsync(
                        batchContext,
                        parsed.LayerId,
                        CreateFeatureRequest(request.Body),
                        cancellationToken),

                ODataResourceKind.Feature when request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    parsed.LayerId.HasValue &&
                    parsed.ObjectId.HasValue =>
                    await ExecuteFeatureGetAsync(
                        crudHandler,
                        batchContext,
                        parsed,
                        query,
                        cancellationToken),

                ODataResourceKind.Feature when request.Method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) &&
                    parsed.LayerId.HasValue &&
                    parsed.ObjectId.HasValue &&
                    parsed.Tail == ODataPathTailKind.None =>
                    await crudHandler.HandleUpdateFeatureAsync(
                        batchContext,
                        parsed.LayerId.Value,
                        parsed.ObjectId.Value,
                        CreateFeatureRequest(request.Body),
                        cancellationToken),

                ODataResourceKind.Feature when request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) &&
                    parsed.LayerId.HasValue &&
                    parsed.ObjectId.HasValue &&
                    parsed.Tail == ODataPathTailKind.None =>
                    await crudHandler.HandleReplaceFeatureAsync(
                        batchContext,
                        parsed.LayerId.Value,
                        parsed.ObjectId.Value,
                        CreateFeatureRequest(request.Body),
                        cancellationToken),

                ODataResourceKind.Feature when request.Method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) &&
                    parsed.LayerId.HasValue &&
                    parsed.ObjectId.HasValue &&
                    parsed.Tail == ODataPathTailKind.None =>
                    await crudHandler.HandleDeleteFeatureAsync(
                        batchContext,
                        parsed.LayerId.Value,
                        parsed.ObjectId.Value,
                        cancellationToken),

                _ => CreateUnsupportedBatchRequestResult(batchContext, request, parsed)
            };

            return await CreateBatchResponseItemAsync(request.Id, batchContext, result);
        }
        catch (ArgumentException ex)
        {
            Log.BatchRequestParseFailed(_logger, request.Id, ex);
            return CreateErrorResponse(request.Id, 400, "InvalidRequest", "Invalid request parameters.");
        }
        catch (Exception ex)
        {
            Log.BatchSingleRequestFailed(_logger, request.Id, ex);
            // Use safe error message to avoid leaking internal details
            return CreateErrorResponse(request.Id, 500, "InternalError", "An error occurred while processing the request.");
        }
    }

    private static string NormalizeRequestUrlForParsing(string url)
        => NormalizeBatchTargetUrl(url);

    private static string NormalizeBatchTargetUrl(string url)
    {
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.PathAndQuery.TrimStart('/');
        }

        return trimmed.TrimStart('/');
    }

    private static bool TryNormalizeClientBatchTargetUrl(
        string url,
        out string normalizedUrl,
        out string? errorMessage)
    {
        var trimmed = url.Trim();
        normalizedUrl = trimmed.TrimStart('/');
        errorMessage = null;

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            normalizedUrl = string.Empty;
            errorMessage = AbsoluteBatchRequestUrlMessage;
            return false;
        }

        return true;
    }

    private static IResult CreateUnsupportedBatchRequestResult(
        HttpContext context,
        ODataBatchRequestItem request,
        ODataParsedPath parsed)
    {
        if (parsed.Tail != ODataPathTailKind.None &&
            !request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return ODataUtilityService.CreateODataError(
                context,
                "MethodNotAllowed",
                "Only GET is supported for $ref and $value requests.",
                StatusCodes.Status405MethodNotAllowed);
        }

        return ODataUtilityService.CreateODataError(
            context,
            "MethodNotAllowed",
            $"Method {request.Method} is not supported for '{request.Url}'.",
            StatusCodes.Status405MethodNotAllowed);
    }

    private static async Task<IResult> ExecuteFeatureGetAsync(
        ODataCrudHandler crudHandler,
        HttpContext context,
        ODataParsedPath parsed,
        IQueryCollection query,
        CancellationToken cancellationToken)
    {
        return parsed.Tail switch
        {
            ODataPathTailKind.None => await crudHandler.HandleGetSingleFeatureAsync(
                context,
                parsed.LayerId!.Value,
                parsed.ObjectId!.Value,
                GetQueryValue(query, "$select"),
                GetQueryValue(query, "$format"),
                cancellationToken),
            ODataPathTailKind.Ref => await crudHandler.HandleGetFeatureReferenceAsync(
                context,
                parsed.LayerId!.Value,
                parsed.ObjectId!.Value,
                cancellationToken),
            ODataPathTailKind.Value => await crudHandler.HandleGetFeatureValueAsync(
                context,
                parsed.LayerId!.Value,
                parsed.ObjectId!.Value,
                GetQueryValue(query, "$format"),
                cancellationToken),
            _ => ODataUtilityService.CreateODataError(
                context,
                "InvalidRequest",
                $"Unsupported path segment '{parsed.Tail}'.")
        };
    }

    private static DefaultHttpContext CreateBatchRequestContext(
        HttpContext parent,
        ODataBatchRequestItem request,
        CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = parent.RequestServices,
            User = parent.User,
            TraceIdentifier = $"{parent.TraceIdentifier}:{request.Id}"
        };

        foreach (var item in parent.Items)
        {
            context.Items[item.Key] = item.Value;
        }

        ODataBatchContext.SuppressMutationSideEffects(context);

        context.Request.Method = request.Method.ToUpperInvariant();
        context.Request.Scheme = parent.Request.Scheme;
        context.Request.Host = parent.Request.Host;
        context.Request.PathBase = parent.Request.PathBase;
        context.Request.Protocol = parent.Request.Protocol;
        context.RequestAborted = cancellationToken;
        context.Response.Body = new MemoryStream();

        var normalizedUrl = NormalizeBatchTargetUrl(request.Url);
        var prefixedPath = normalizedUrl.StartsWith("odata/", StringComparison.OrdinalIgnoreCase)
            ? $"/{normalizedUrl}"
            : $"/odata/{normalizedUrl}";
        var separator = prefixedPath.IndexOf('?', StringComparison.Ordinal);
        var path = separator >= 0 ? prefixedPath[..separator] : prefixedPath;
        var queryString = separator >= 0 ? prefixedPath[separator..] : string.Empty;

        context.Request.Path = path;
        context.Request.QueryString = string.IsNullOrEmpty(queryString)
            ? QueryString.Empty
            : new QueryString(queryString);

        if (request.Headers != null)
        {
            foreach (var header in request.Headers)
            {
                context.Request.Headers[header.Key] = header.Value;
            }
        }

        return context;
    }

    private static async Task<ODataBatchResponseItem> CreateBatchResponseItemAsync(
        string requestId,
        HttpContext context,
        IResult result)
    {
        await result.ExecuteAsync(context);

        var headers = context.Response.Headers
            .Where(static header =>
                !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                !header.Key.Equals("OData-Version", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(static header => header.Key, static header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        object? body = null;
        if (context.Response.Body is MemoryStream memoryStream && memoryStream.Length > 0)
        {
            memoryStream.Position = 0;
            using var reader = new StreamReader(memoryStream, leaveOpen: true);
            var payload = await reader.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(payload))
            {
                body = context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                    ? ReadJsonPayload(payload)
                    : payload;
            }
        }

        return new ODataBatchResponseItem
        {
            Id = requestId,
            Status = context.Response.StatusCode,
            Headers = headers.Count == 0 ? null : headers,
            Body = body
        };
    }

    private static object? ReadJsonPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return JsonElementConverter.ConvertToObject(document.RootElement.Clone());
    }

    private static string? GetQueryValue(IQueryCollection query, string key)
        => ODataRequestValidation.GetQueryValue(query, key);

    private static ODataFeatureRequest CreateFeatureRequest(Dictionary<string, object?>? body)
    {
        if (body == null)
        {
            return new ODataFeatureRequest();
        }

        var element = JsonSerializer.SerializeToElement(body, ODataJsonContext.Default.DictionaryStringObject);
        return JsonSerializer.Deserialize(element, ODataJsonContext.Default.ODataFeatureRequest) ?? new ODataFeatureRequest();
    }

    private static bool TryResolveContentIdReferences(
        ODataBatchRequestItem request,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out ODataBatchRequestItem resolvedRequest,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!TryResolveRequestUrl(request.Url, responsesById, out var resolvedUrl, out errorMessage))
        {
            resolvedRequest = request;
            return false;
        }

        if (!TryResolveBodyReferences(request.Body, responsesById, out var resolvedBody, out errorMessage))
        {
            resolvedRequest = request;
            return false;
        }

        resolvedRequest = new ODataBatchRequestItem
        {
            Id = request.Id,
            Method = request.Method,
            Url = resolvedUrl,
            Headers = request.Headers,
            Body = resolvedBody,
            AtomicityGroup = request.AtomicityGroup,
            DependsOn = request.DependsOn
        };

        return true;
    }

    private static bool TryResolveRequestUrl(
        string url,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out string resolvedUrl,
        out string? errorMessage)
    {
        resolvedUrl = url;
        errorMessage = null;

        var trimmed = url.Trim();
        if (!trimmed.StartsWith('$'))
        {
            return TryNormalizeClientBatchTargetUrl(trimmed, out resolvedUrl, out errorMessage);
        }

        var delimiterIndex = trimmed.IndexOfAny(['/', '?']);
        var referenceId = delimiterIndex >= 0 ? trimmed[1..delimiterIndex] : trimmed[1..];
        var suffix = delimiterIndex >= 0 ? trimmed[delimiterIndex..] : string.Empty;

        if (!TryResolveContentIdTarget(referenceId, responsesById, out var targetUrl, out errorMessage))
        {
            return false;
        }

        resolvedUrl = $"{NormalizeBatchTargetUrl(targetUrl)}{suffix}";
        return true;
    }

    private static bool TryResolveBodyReferences(
        Dictionary<string, object?>? body,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out Dictionary<string, object?>? resolvedBody,
        out string? errorMessage)
    {
        errorMessage = null;
        if (body == null)
        {
            resolvedBody = null;
            return true;
        }

        resolvedBody = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in body)
        {
            if (!TryResolveBodyValue(value, responsesById, out var resolvedValue, out errorMessage))
            {
                return false;
            }

            resolvedBody[key] = resolvedValue;
        }

        return true;
    }

    private static bool TryResolveBodyValue(
        object? value,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out object? resolvedValue,
        out string? errorMessage)
    {
        errorMessage = null;
        resolvedValue = value;

        if (value is string stringValue &&
            stringValue.StartsWith('$') &&
            !stringValue.Contains('/', StringComparison.Ordinal) &&
            !stringValue.Contains('?', StringComparison.Ordinal))
        {
            var referenceId = stringValue[1..];
            if (!TryResolveContentIdTarget(referenceId, responsesById, out var targetUrl, out errorMessage))
            {
                resolvedValue = null;
                return false;
            }

            resolvedValue = targetUrl;
            return true;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, nestedValue) in dictionary)
            {
                if (!TryResolveBodyValue(nestedValue, responsesById, out var resolvedNestedValue, out errorMessage))
                {
                    resolvedValue = null;
                    return false;
                }

                result[key] = resolvedNestedValue;
            }

            resolvedValue = result;
            return true;
        }

        if (value is IEnumerable<object?> listValue)
        {
            var resolvedItems = new List<object?>();
            foreach (var item in listValue)
            {
                if (!TryResolveBodyValue(item, responsesById, out var resolvedItem, out errorMessage))
                {
                    resolvedValue = null;
                    return false;
                }

                resolvedItems.Add(resolvedItem);
            }

            resolvedValue = resolvedItems.ToArray();
        }

        return true;
    }

    private static bool TryResolveContentIdTarget(
        string referenceId,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out string targetUrl,
        out string? errorMessage)
    {
        targetUrl = string.Empty;
        errorMessage = null;

        if (!responsesById.TryGetValue(referenceId, out var referencedResponse))
        {
            errorMessage = $"Unknown Content-ID reference '${referenceId}'.";
            return false;
        }

        if (referencedResponse.Status >= 400)
        {
            errorMessage = $"Content-ID reference '${referenceId}' points to a failed request.";
            return false;
        }

        if (referencedResponse.Headers != null &&
            (referencedResponse.Headers.TryGetValue("EntityId", out var entityId) ||
             referencedResponse.Headers.TryGetValue("OData-EntityId", out entityId)) &&
            !string.IsNullOrWhiteSpace(entityId))
        {
            targetUrl = NormalizeBatchTargetUrl(entityId);
            return true;
        }

        if (referencedResponse.Headers != null &&
            referencedResponse.Headers.TryGetValue("Location", out var location) &&
            !string.IsNullOrWhiteSpace(location))
        {
            targetUrl = NormalizeBatchTargetUrl(location);
            return true;
        }

        if (referencedResponse.Body is Dictionary<string, object?> dictionary &&
            TryGetInt(dictionary, "LayerId", out var layerId) &&
            TryGetLong(dictionary, "ObjectId", out var objectId))
        {
            targetUrl = $"/odata/Features(LayerId={layerId},ObjectId={objectId})";
            return true;
        }

        errorMessage = $"Content-ID reference '${referenceId}' does not identify an entity resource.";
        return false;
    }

    private static bool TryGetInt(Dictionary<string, object?> values, string key, out int result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) && value switch
        {
            int i => (result = i) >= 0,
            long l when l is >= int.MinValue and <= int.MaxValue => (result = (int)l) >= 0,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) >= 0,
            _ => false
        };
    }

    private static bool TryGetLong(Dictionary<string, object?> values, string key, out long result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) && value switch
        {
            long l => (result = l) >= 0,
            int i => (result = i) >= 0,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) >= 0,
            _ => false
        };
    }

    private async Task<ODataBatchResponseItem> HandleGetAsync(
        string requestId,
        LayerDefinition layer,
        long? objectId,
        string requestUrl,
        ODataPathTailKind tailKind,
        string baseUrl,
        CancellationToken cancellationToken)
    {
        if (!objectId.HasValue)
        {
            if (tailKind != ODataPathTailKind.None)
            {
                return CreateErrorResponse(requestId, 400, "InvalidRequest", "$ref and $value require an entity key.");
            }

            if (!TryParseCollectionQueryOptions(
                requestUrl,
                out var top,
                out var skip,
                out var count,
                out var select,
                out var parseError))
            {
                return CreateErrorResponse(requestId, 400, "InvalidRequest", parseError ?? "Invalid collection query options.");
            }

            var effectiveTop = top ?? DefaultBatchCollectionTop;
            var effectiveSkip = skip ?? 0;
            var query = new FeatureQuery
            {
                Limit = effectiveTop,
                Offset = effectiveSkip,
                SpatialReferenceSrid = layer.SpatialReference.ToSrid()
            };

            var queryResult = await _featureReader.QueryAsync(layer.Id, query, cancellationToken);
            var collectionAxisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
            var values = queryResult.Items.Select(feature =>
            {
                var payload = FeatureToBody(feature, layer, collectionAxisOrder, baseUrl, out _);
                if (!string.IsNullOrWhiteSpace(select))
                {
                    payload = ODataUtilityService.ApplySelect(payload, select);
                }

                return (object)payload;
            }).ToArray();

            var response = new ODataResponse
            {
                Context = ODataUtilityService.BuildContextUrl(baseUrl, "Features", select: select),
                Count = count == true ? queryResult.TotalCount : null,
                Value = values,
                NextLink = null
            };

            return CreateSuccessResponse(requestId, 200, response);
        }

        var feature = await _featureReader.GetAsync(layer.Id, objectId.Value, cancellationToken);
        if (!feature.HasValue)
        {
            return CreateErrorResponse(requestId, 404, "ResourceNotFound", $"Feature {objectId} not found in layer {layer.Id}.");
        }

        var axisOrder = await ResolveAxisOrderAsync(layer, cancellationToken);
        var payload = FeatureToBody(feature.Value, layer, axisOrder, baseUrl, out var etag);

        return tailKind switch
        {
            ODataPathTailKind.None => CreateSuccessResponse(
                requestId,
                200,
                payload,
                new Dictionary<string, string> { ["ETag"] = etag }),
            ODataPathTailKind.Ref => CreateSuccessResponse(
                requestId,
                200,
                new Dictionary<string, object?>
                {
                    ["@odata.id"] = ODataUtilityService.CreateLocationHeader(baseUrl, layer.Id, objectId.Value),
                    ["@odata.editLink"] = ODataUtilityService.CreateLocationHeader(baseUrl, layer.Id, objectId.Value)
                }),
            ODataPathTailKind.Value => CreateSuccessResponse(
                requestId,
                200,
                payload,
                new Dictionary<string, string> { ["ETag"] = etag }),
            _ => CreateErrorResponse(requestId, 400, "InvalidRequest", $"Unsupported path segment '{tailKind}'.")
        };
    }

    private static async Task<ODataBatchResponseItem?> ValidateBatchRequestAccessAsync(
        HttpContext context,
        string requestId,
        LayerDefinition layer,
        ServiceDefinition? service,
        string method,
        CancellationToken cancellationToken)
    {
        var scope = IsMutationMethod(method) ? AccessScope.Write : AccessScope.Read;
        var decision = AccessPolicyHelpers.EvaluateAccess(
            context,
            layer.Metadata?.AccessPolicy,
            service?.Metadata?.AccessPolicy,
            scope);
        if (!decision.IsAllowed)
        {
            var statusCode = decision.RequiresAuthentication
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status403Forbidden;

            return CreateErrorResponse(
                requestId,
                statusCode,
                decision.RequiresAuthentication ? "Unauthorized" : "Forbidden",
                decision.FailureReason
                    ?? (decision.RequiresAuthentication
                        ? "Authentication is required to access one or more requested layers."
                        : "Access to one or more requested layers is forbidden."));
        }

        if (scope == AccessScope.Write)
        {
            var rbacResult = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                context,
                layer,
                service,
                cancellationToken);
            if (rbacResult != null)
            {
                var statusCode = rbacResult is IStatusCodeHttpResult statusCodeResult && statusCodeResult.StatusCode.HasValue
                    ? statusCodeResult.StatusCode.Value
                    : StatusCodes.Status403Forbidden;

                return CreateErrorResponse(
                    requestId,
                    statusCode,
                    statusCode == StatusCodes.Status401Unauthorized ? "Unauthorized" : "Forbidden",
                    statusCode == StatusCodes.Status401Unauthorized
                        ? "Authentication is required to access one or more requested layers."
                        : "Access to one or more requested layers is forbidden.");
            }
        }

        return null;
    }

    private static bool IsMutationMethod(string? method)
    {
        return method != null &&
               (method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PUT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsODataEnabled(
        LayerDefinition layer,
        ServiceDefinition? service)
    {
        return service == null
            ? ServiceProtocols.IsProtocolEnabled(layer.Metadata, ServiceProtocols.OData)
            : ServiceProtocols.IsProtocolEnabled(service.Metadata, ServiceProtocols.OData);
    }

    private static ODataParsedPath ParseUrl(string url)
    {
        if (!ODataPathParser.TryParse(url, out var parsed, out var errorMessage))
        {
            throw new ArgumentException(errorMessage ?? "Invalid OData URL.");
        }

        return parsed;
    }

    private static bool TryParseCollectionQueryOptions(
        string requestUrl,
        out int? top,
        out int? skip,
        out bool? count,
        out string? select,
        out string? errorMessage)
    {
        top = null;
        skip = null;
        count = null;
        select = null;
        errorMessage = null;

        var queryString = ExtractQueryString(requestUrl);
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return true;
        }

        var query = QueryHelpers.ParseQuery(queryString);
        foreach (var parameter in query)
        {
            var key = parameter.Key;
            var value = parameter.Value.ToString();

            switch (key.ToLowerInvariant())
            {
                case "$top":
                case "top":
                    if (!ODataParsingUtilities.TryParseOptionalInt(value, "$top", out var topValue, out var topError))
                    {
                        errorMessage = topError;
                        return false;
                    }

                    top = topValue;
                    break;

                case "$skip":
                case "skip":
                    if (!ODataParsingUtilities.TryParseOptionalInt(value, "$skip", out var skipValue, out var skipError))
                    {
                        errorMessage = skipError;
                        return false;
                    }

                    skip = skipValue;
                    break;

                case "$skiptoken":
                case "skiptoken":
                    if (!ODataParsingUtilities.TryParseOptionalInt(value, "$skiptoken", out var skipTokenValue, out var skipTokenError))
                    {
                        errorMessage = skipTokenError;
                        return false;
                    }

                    if (skip.HasValue && skipTokenValue.HasValue)
                    {
                        errorMessage = "$skip and $skiptoken cannot be used together.";
                        return false;
                    }

                    if (skipTokenValue.HasValue)
                    {
                        skip = skipTokenValue;
                    }

                    break;

                case "$count":
                case "count":
                    if (!ODataParsingUtilities.TryParseOptionalBool(value, "$count", out var countValue, out var countError))
                    {
                        errorMessage = countError;
                        return false;
                    }

                    count = countValue;
                    break;

                case "$select":
                case "select":
                    select = value;
                    break;

                default:
                    errorMessage = $"Unsupported query option '{key}' for batch collection GET.";
                    return false;
            }
        }

        if (top.HasValue && (top.Value < 0 || top.Value > DefaultBatchCollectionTop))
        {
            errorMessage = $"$top must be between 0 and {DefaultBatchCollectionTop.ToString(CultureInfo.InvariantCulture)}.";
            return false;
        }

        if (skip.HasValue && skip.Value < 0)
        {
            errorMessage = "$skip/$skiptoken cannot be negative.";
            return false;
        }

        return true;
    }

    private static string? ExtractQueryString(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.Query.TrimStart('?');
        }

        var separator = url.IndexOf('?', StringComparison.Ordinal);
        return separator >= 0 && separator < url.Length - 1
            ? url[(separator + 1)..]
            : null;
    }

    private async Task<(FeatureEditBatch? Batch, string? ErrorMessage)> BuildValidatedEditBatchAsync(
        LayerDefinition layer,
        ODataEditRequest request,
        bool rollbackOnFailure,
        CancellationToken cancellationToken)
    {
        var conversion = await _editParameterAdapter.ConvertAsync(request, layer, cancellationToken);
        if (!conversion.IsSuccess || conversion.EditRequest == null || conversion.Transaction == null)
        {
            return (null, conversion.ErrorMessage ?? "Invalid OData edit request.");
        }

        var validation = _editProcessor.ValidateEdit(conversion.EditRequest.Value, layer);
        if (!validation.IsValid)
        {
            return (null, validation.ErrorMessage ?? "Invalid OData edit request.");
        }

        var transactionValidation = _editProcessor.ValidateTransaction(conversion.Transaction.Value, layer);
        if (!transactionValidation.IsValid)
        {
            return (null, transactionValidation.ErrorMessage ?? "Invalid OData edit request.");
        }

        var optimizedRequest = _editProcessor.OptimizeEdit(conversion.EditRequest.Value, layer);
        var editBatch = _editProcessor.ToFeatureEditBatch(optimizedRequest, layer) with
        {
            RollbackOnFailure = rollbackOnFailure
        };

        return (editBatch, null);
    }

    private static ODataEditRequest CreateEditRequestFromFeature(
        ODataOperation operation,
        Feature feature,
        long? objectId = null,
        string? ifMatch = null,
        string? ifNoneMatch = null)
        => new()
        {
            Operation = operation,
            ObjectId = objectId,
            IfMatch = ifMatch,
            IfNoneMatch = ifNoneMatch,
            GeometryWkb = feature.Geometry,
            Payload = new ParsedFeaturePayload
            {
                Attributes = new Dictionary<string, object?>(feature.Attributes, StringComparer.OrdinalIgnoreCase)
            }
        };

    /// <summary>
    /// Returns whether the OData request body explicitly specified a Geometry property.
    /// PATCH and POST both run through CreateFeatureFromBodyAsync, which preserves the
    /// existing row's geometry on partial-update requests; reading the post-merge feature
    /// would report attribute-only PATCH as a geometry change. This helper parses the
    /// raw body once so the outbox queue can be seeded with the request's actual intent.
    /// Parse failures return false because the same body will fail
    /// <c>CreateFeatureFromBodyAsync</c> and the request will not reach the outbox queue.
    /// </summary>
    private static bool TryGetGeometrySpecified(Dictionary<string, object?>? body)
    {
        if (body is null || body.Count == 0)
        {
            return false;
        }

        return ODataFeaturePayloadParser.TryParse(body, out var payload, out _)
            && payload.GeometrySpecified;
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
        if (payload.GeometrySpecified && payload.Geometry != null)
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
        else if (payload.GeometrySpecified)
        {
            geometry = null;
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
        Dictionary<string, string>? headers = null,
        Feature? mutationFeature = null)
    {
        return new ODataBatchResponseItem
        {
            Id = id,
            Status = status,
            Headers = headers,
            Body = body,
            MutationFeature = mutationFeature
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

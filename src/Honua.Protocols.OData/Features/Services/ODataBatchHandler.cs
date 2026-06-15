// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Handles OData v4 $batch operations with support for atomicity groups.
/// </summary>
/// <remarks>
/// Implementation is split across partials for maintainability:
/// <list type="bullet">
/// <item><see href="ODataBatchHandler.Dependencies.cs"/> — dependency ordering and Content-ID resolution.</item>
/// <item><see href="ODataBatchHandler.ChangeSet.cs"/> — atomicity-group (change-set) processing.</item>
/// <item><see href="ODataBatchHandler.Dispatch.cs"/> — per-request dispatch, URL normalization, path parsing, access validation.</item>
/// <item><see href="ODataBatchHandler.Edit.cs"/> — edit-batch construction, body materialization, precondition checks.</item>
/// <item><see href="ODataBatchHandler.Response.cs"/> — response assembly, ETag computation, error formatting.</item>
/// </list>
/// </remarks>
internal sealed partial class ODataBatchHandler
{
    private const string NonAtomicGroupKey = "__non-atomic__";
    private const string AtomicGroupPrefix = "atomic:";
    private const int DefaultBatchCollectionTop = 1000;
    private const string AbsoluteBatchRequestUrlMessage = "Absolute batch request URLs are not supported.";
    private const string ODataProtocol = "OData";

    private readonly IFeatureReader _featureReader;
    private readonly IFeatureWriter _featureWriter;
    private readonly IGeometryService _geometryService;
    private readonly ICrsRegistry _crsRegistry;
    private readonly FeatureMutationValidator _mutationValidator;
    private readonly EditLimits _editLimits;
    private readonly IETagService _etagService;
    private readonly IEditParameterAdapter<ODataEditRequest> _editParameterAdapter;
    private readonly IEditProcessor _editProcessor;
    private readonly Honua.Infrastructure.Events.FeatureMutationEventService _mutationEventService;
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

    private sealed record ODataBatchLayerContext(
        int PublicLayerId,
        int StorageLayerId,
        int Srid,
        MetadataV2Publication? Publication,
        MetadataV2Resource Resource,
        MetadataV2Service? Service);

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

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;

namespace Honua.Server.Features.OData;

/// <summary>
/// Handler for OData $batch operations with atomicity group support and cache invalidation.
/// Coordinates batch processing and manages transaction boundaries for feature operations.
/// </summary>
internal sealed partial class ODataBatchOperationHandler(
    ODataBatchDependencies batchDependencies,
    ODataValidationService validationService,
    IETagService etagService,
    ILogger<ODataBatchOperationHandler> logger)
{
    private readonly ODataBatchDependencies _batchDependencies = batchDependencies ?? throw new ArgumentNullException(nameof(batchDependencies));
    private readonly ODataValidationService _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
    private readonly IETagService _etagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
    private readonly ILogger<ODataBatchOperationHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles OData $batch request for executing multiple operations.
    /// </summary>
    public async Task<IResult> HandleBatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _validationService,
                AllowedQueryParameters.None);
            if (queryValidation != null)
            {
                return queryValidation;
            }

            var effectiveToken = ODataUtilityService.GetTimeoutAwareCancellationToken(context);
            var baseUrl = ODataUtilityService.GetBaseUrl(context.Request);

            // Read and parse the batch request
            ODataBatchRequest? batchRequest;
            try
            {
                batchRequest = await context.Request.ReadFromJsonAsync<ODataBatchRequest>(
                    ODataJsonContext.Default.ODataBatchRequest, effectiveToken);
            }
            catch (System.Text.Json.JsonException ex)
            {
                Log.BatchParseFailed(_logger, ex);
                return ODataUtilityService.CreateODataError(context, "InvalidRequest", "Failed to parse batch request body.");
            }

            if (batchRequest == null || batchRequest.Requests.IsDefaultOrEmpty)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidRequest",
                    "Batch request must contain at least one request.");
            }

            ODataLog.BatchRequested(_logger, batchRequest.Requests.Length);

            var accessError = await ValidateBatchAccessAsync(context, batchRequest, effectiveToken);
            if (accessError != null)
            {
                return accessError;
            }

            // Process the batch
            var handler = new ODataBatchHandler(_batchDependencies, _etagService, _logger);
            var response = await handler.ProcessBatchAsync(batchRequest, baseUrl, effectiveToken);

            // Handle cache invalidation for mutated layers
            await InvalidateCacheForBatchAsync(context, batchRequest, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            return Results.Json(response, ODataJsonContext.Default.ODataBatchResponse,
                contentType: ODataUtilityService.GetODataContentType());
        }
        catch (OperationCanceledException)
            when (ODataUtilityService.GetTimeoutAwareCancellationToken(context).IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.BatchFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the batch request", 500);
        }
    }

    private async Task<IResult?> ValidateBatchAccessAsync(
        HttpContext context,
        ODataBatchRequest batchRequest,
        CancellationToken cancellationToken)
    {
        var layerCache = new Dictionary<int, LayerDefinition?>();
        var requiresAuth = false;
        var hasDenied = false;

        foreach (var request in batchRequest.Requests)
        {
            if (!TryResolveLayerId(request, out var layerId))
            {
                continue;
            }

            if (!layerCache.TryGetValue(layerId, out var layer))
            {
                layer = await _batchDependencies.LayerCatalog.GetLayerAsync(layerId, cancellationToken);
                layerCache[layerId] = layer;
            }

            if (layer == null)
            {
                continue;
            }

            var scope = IsMutationMethod(request.Method) ? AccessScope.Write : AccessScope.Read;
            var decision = AccessPolicyHelpers.EvaluateAccess(context, layer.Metadata?.AccessPolicy, servicePolicy: null, scope: scope);
            if (decision.IsAllowed)
            {
                continue;
            }

            hasDenied = true;
            if (decision.RequiresAuthentication)
            {
                requiresAuth = true;
                break;
            }
        }

        if (!hasDenied)
        {
            return null;
        }

        var detail = requiresAuth
            ? "Authentication is required to access one or more requested layers."
            : "Access to one or more requested layers is forbidden.";

        return requiresAuth
            ? StandardErrorHelpers.CreateUnauthorized(context, detail)
            : StandardErrorHelpers.CreateForbidden(context, detail);
    }

    /// <summary>
    /// Invalidates cache for layers modified in the batch operation
    /// </summary>
    private static async Task InvalidateCacheForBatchAsync(
        HttpContext context,
        ODataBatchRequest batchRequest,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            var mutatedLayers = CollectMutationLayerIds(batchRequest);
            if (mutatedLayers.Count > 0)
            {
                foreach (var layerId in mutatedLayers)
                {
                    await cacheInvalidator.InvalidateLayerAsync(null, layerId, cancellationToken);
                }
            }
            else if (ContainsMutation(batchRequest))
            {
                await cacheInvalidator.InvalidateOgcMetadataAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Checks if the batch contains any mutation operations
    /// </summary>
    private static bool ContainsMutation(ODataBatchRequest batchRequest)
    {
        foreach (var request in batchRequest.Requests)
        {
            if (IsMutationMethod(request.Method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collects layer IDs from mutation operations in the batch
    /// </summary>
    private static HashSet<int> CollectMutationLayerIds(ODataBatchRequest batchRequest)
    {
        var layerIds = new HashSet<int>();

        foreach (var request in batchRequest.Requests)
        {
            if (!IsMutationMethod(request.Method))
            {
                continue;
            }

            if (TryResolveLayerId(request, out var layerId))
            {
                layerIds.Add(layerId);
            }
        }

        return layerIds;
    }

    /// <summary>
    /// Determines if the HTTP method is a mutation operation
    /// </summary>
    private static bool IsMutationMethod(string? method)
    {
        return method != null &&
               (method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PATCH", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                method.Equals("PUT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Attempts to resolve layer ID from an OData batch request
    /// </summary>
    private static bool TryResolveLayerId(ODataBatchRequestItem request, out int layerId)
    {
        layerId = default;

        if (!ODataPathParser.TryParse(request.Url, out var parsed, out _))
        {
            return false;
        }

        if (parsed.LayerId.HasValue)
        {
            layerId = parsed.LayerId.Value;
            return true;
        }

        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) && request.Body != null)
        {
            if (ODataFeaturePayloadParser.TryParse(request.Body, out var payload, out _))
            {
                if (payload.LayerId.HasValue)
                {
                    layerId = payload.LayerId.Value;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Logging methods for OData batch operations.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 3011, Level = LogLevel.Warning, Message = "OData batch request parse failed.")]
        public static partial void BatchParseFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 3012, Level = LogLevel.Error, Message = "OData batch request failed.")]
        public static partial void BatchFailed(ILogger logger, Exception exception);
    }
}

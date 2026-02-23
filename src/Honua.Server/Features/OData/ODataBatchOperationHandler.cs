// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

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
            var (batchRequest, isMultipartRequest, parseError) = await ParseBatchRequestAsync(context.Request, effectiveToken);

            if (batchRequest == null)
            {
                return ODataUtilityService.CreateODataError(context, "InvalidRequest",
                    parseError ?? "Failed to parse batch request body.");
            }

            if (batchRequest.Requests.IsDefaultOrEmpty)
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

            if (isMultipartRequest)
            {
                var boundary = $"batchresponse_{Guid.NewGuid():N}";
                var payload = CreateMultipartBatchResponsePayload(response, boundary);
                return Results.Text(payload, $"multipart/mixed;boundary={boundary}");
            }

            return Results.Json(response, ODataJsonContext.Default.ODataBatchResponse,
                contentType: ODataUtilityService.GetODataContentType(context.Request, format: null));
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

    private async Task<(ODataBatchRequest? Request, bool IsMultipart, string? Error)> ParseBatchRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (IsMultipartContentType(request.ContentType))
        {
            var multipartResult = await ParseMultipartBatchRequestAsync(request, cancellationToken);
            return (multipartResult.Request, true, multipartResult.Error);
        }

        try
        {
            var requestModel = await request.ReadFromJsonAsync<ODataBatchRequest>(
                ODataJsonContext.Default.ODataBatchRequest,
                cancellationToken);
            return (requestModel, false, null);
        }
        catch (JsonException ex)
        {
            Log.BatchParseFailed(_logger, ex);
            return (null, false, "Failed to parse batch request body.");
        }
    }

    private static bool IsMultipartContentType(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.StartsWith("multipart/mixed", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(ODataBatchRequest? Request, string? Error)> ParseMultipartBatchRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType))
        {
            return (null, "Invalid multipart batch content type.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return (null, "Multipart batch request is missing the boundary parameter.");
        }

        var reader = new MultipartReader(boundary, request.Body);
        var requests = new List<ODataBatchRequestItem>();
        var requestSequence = 1;
        var atomicitySequence = 1;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) != null)
        {
            var sectionContentType = GetSectionContentType(section);
            if (IsApplicationHttpContentType(sectionContentType))
            {
                var fallbackId = requestSequence.ToString(CultureInfo.InvariantCulture);
                var parse = await ParseApplicationHttpSectionAsync(section, null, fallbackId, cancellationToken);
                if (parse.Request == null)
                {
                    return (null, parse.Error);
                }

                requests.Add(parse.Request);
                requestSequence++;
                continue;
            }

            if (!IsMultipartContentType(sectionContentType))
            {
                return (null, $"Unsupported multipart section content type '{sectionContentType ?? "(missing)"}'.");
            }

            var groupId = $"changeset-{atomicitySequence.ToString(CultureInfo.InvariantCulture)}";
            atomicitySequence++;

            var changeset = await ParseChangesetSectionAsync(section, groupId, requestSequence, cancellationToken);
            if (changeset.Requests == null)
            {
                return (null, changeset.Error);
            }

            requests.AddRange(changeset.Requests);
            requestSequence = changeset.NextRequestSequence;
        }

        return (new ODataBatchRequest { Requests = requests.ToImmutableArray() }, null);
    }

    private async Task<(List<ODataBatchRequestItem>? Requests, int NextRequestSequence, string? Error)> ParseChangesetSectionAsync(
        MultipartSection section,
        string atomicityGroup,
        int requestSequence,
        CancellationToken cancellationToken)
    {
        var sectionContentType = GetSectionContentType(section);
        if (!MediaTypeHeaderValue.TryParse(sectionContentType, out var sectionMediaType))
        {
            return (null, requestSequence, "Invalid changeset content type.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(sectionMediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return (null, requestSequence, "Changeset is missing the boundary parameter.");
        }

        var reader = new MultipartReader(boundary, section.Body);
        var requests = new List<ODataBatchRequestItem>();

        MultipartSection? changesetSection;
        while ((changesetSection = await reader.ReadNextSectionAsync(cancellationToken)) != null)
        {
            var changesetContentType = GetSectionContentType(changesetSection);
            if (!IsApplicationHttpContentType(changesetContentType))
            {
                return (null, requestSequence, "Changeset can only contain application/http sections.");
            }

            var fallbackId = requestSequence.ToString(CultureInfo.InvariantCulture);
            var parse = await ParseApplicationHttpSectionAsync(changesetSection, atomicityGroup, fallbackId, cancellationToken);
            if (parse.Request == null)
            {
                return (null, requestSequence, parse.Error);
            }

            requests.Add(parse.Request);
            requestSequence++;
        }

        return (requests, requestSequence, null);
    }

    private async Task<(ODataBatchRequestItem? Request, string? Error)> ParseApplicationHttpSectionAsync(
        MultipartSection section,
        string? atomicityGroup,
        string fallbackRequestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var reader = new StreamReader(section.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, "application/http section payload is empty.");
        }

        var normalized = payload.Replace("\r\n", "\n", StringComparison.Ordinal);
        var separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        var headerBlock = separator >= 0 ? normalized[..separator] : normalized;
        var bodyBlock = separator >= 0 ? normalized[(separator + 2)..].Trim() : string.Empty;

        var lines = headerBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            return (null, "Invalid application/http section: missing request line.");
        }

        var requestLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestLineParts.Length < 2)
        {
            return (null, "Invalid application/http request line.");
        }

        var method = requestLineParts[0];
        var url = NormalizeBatchUrl(requestLineParts[1]);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                headers[key] = value;
            }
        }

        Dictionary<string, object?>? body = null;
        if (!string.IsNullOrWhiteSpace(bodyBlock))
        {
            try
            {
                body = JsonSerializer.Deserialize(bodyBlock, ODataJsonContext.Default.DictionaryStringObject);
                if (body == null)
                {
                    return (null, "Batch operation body must be a JSON object.");
                }
            }
            catch (JsonException)
            {
                return (null, "Failed to parse JSON body in application/http section.");
            }
        }

        var requestId = fallbackRequestId;
        if (section.Headers != null &&
            section.Headers.TryGetValue("Content-ID", out var contentIdValues))
        {
            var contentId = contentIdValues.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(contentId))
            {
                requestId = contentId;
            }
        }

        return (new ODataBatchRequestItem
        {
            Id = requestId,
            Method = method,
            Url = url,
            Headers = headers.Count > 0 ? headers : null,
            Body = body,
            AtomicityGroup = atomicityGroup
        }, null);
    }

    private static string? GetSectionContentType(MultipartSection section)
    {
        if (!string.IsNullOrWhiteSpace(section.ContentType))
        {
            return section.ContentType;
        }

        if (section.Headers != null &&
            section.Headers.TryGetValue(HeaderNames.ContentType, out var values))
        {
            return values.ToString();
        }

        return null;
    }

    private static bool IsApplicationHttpContentType(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.StartsWith("application/http", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBatchUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery.TrimStart('/');
        }

        return url.Trim().TrimStart('/');
    }

    private static string CreateMultipartBatchResponsePayload(ODataBatchResponse response, string boundary)
    {
        var builder = new StringBuilder();

        foreach (var item in response.Responses)
        {
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Type: application/http\r\n");
            builder.Append("Content-Transfer-Encoding: binary\r\n\r\n");
            builder.Append("HTTP/1.1 ")
                .Append(item.Status.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(ReasonPhrases.GetReasonPhrase(item.Status))
                .Append("\r\n");

            builder.Append("OData-Version: 4.0\r\n");
            if (item.Headers != null)
            {
                foreach (var header in item.Headers)
                {
                    builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }
            }

            string? bodyJson = null;
            if (item.Body != null)
            {
                bodyJson = SerializeBatchResponseBody(item.Body);
                builder.Append("Content-Type: application/json\r\n");
                builder.Append("Content-Length: ")
                    .Append(Encoding.UTF8.GetByteCount(bodyJson).ToString(CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }

            builder.Append("\r\n");
            if (bodyJson != null)
            {
                builder.Append(bodyJson).Append("\r\n");
            }
        }

        builder.Append("--").Append(boundary).Append("--\r\n");
        return builder.ToString();
    }

    private static string SerializeBatchResponseBody(object body)
    {
        return body switch
        {
            ODataError error => JsonSerializer.Serialize(error, ODataJsonContext.Default.ODataError),
            ODataResponse odataResponse => JsonSerializer.Serialize(odataResponse, ODataJsonContext.Default.ODataResponse),
            Dictionary<string, object?> dictionary => JsonSerializer.Serialize(dictionary, ODataJsonContext.Default.DictionaryStringObject),
            _ => JsonSerializer.Serialize(body, ODataJsonContext.Default.Object)
        };
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
                if (scope == AccessScope.Write)
                {
                    var rbacError = await ServiceDataEditorAuthorization.RequireLayerDataEditorAsync(
                        context,
                        layerId,
                        cancellationToken);
                    if (rbacError != null)
                    {
                        return rbacError;
                    }
                }

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

        [LoggerMessage(EventId = 3022, Level = LogLevel.Error, Message = "OData batch request failed.")]
        public static partial void BatchFailed(ILogger logger, Exception exception);
    }
}

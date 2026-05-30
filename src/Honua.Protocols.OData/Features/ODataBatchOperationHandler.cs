// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Models;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Honua.Protocols.OData;

/// <summary>
/// Handler for OData $batch operations with atomicity group support and cache invalidation.
/// Coordinates batch processing and manages transaction boundaries for feature operations.
/// </summary>
internal sealed partial class ODataBatchOperationHandler(
    ODataBatchDependencies batchDependencies,
    ILogger<ODataBatchOperationHandler> logger)
{
    private const int MaxBatchOperationCount = 1000;
    private const int MaxApplicationHttpSectionBytes = 10 * 1024 * 1024;
    private const string BatchTooManyOperationsMessage =
        "Batch request exceeds the maximum of 1000 operations.";
    private const string BatchOperationPayloadTooLargeMessage =
        "Batch operation payload exceeds the maximum allowed size of 10485760 bytes.";

    private readonly ODataBatchDependencies _batchDependencies = batchDependencies ?? throw new ArgumentNullException(nameof(batchDependencies));
    private readonly ILogger<ODataBatchOperationHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Handles OData $batch request for executing multiple operations.
    /// </summary>
    public async Task<IResult> HandleBatchRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        Activity? activity = null;
        try
        {
            activity = HonuaTelemetry.ActivitySource.StartActivity(
                HonuaTelemetry.Activities.FeatureEdit,
                ActivityKind.Internal);
            activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.OData);
            activity?.SetTag(HonuaTelemetry.Tags.Operation, "batch");

            var queryValidation = ODataRequestValidation.ValidateAllowedParameters(
                context,
                _batchDependencies.ValidationService,
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
            activity?.SetTag(HonuaTelemetry.Tags.FeatureCount, batchRequest.Requests.Length);

            // Process the batch
            var handler = new ODataBatchHandler(_batchDependencies, _batchDependencies.ETagService, _logger);
            var response = await handler.ProcessBatchAsync(context, batchRequest, baseUrl, effectiveToken);

            // Handle cache invalidation for successful mutation responses.
            await InvalidateCacheForBatchAsync(context, batchRequest, response, CancellationToken.None);
            await PublishBatchFeatureEventsAsync(context, batchRequest, response, effectiveToken);

            ODataUtilityService.SetODataHeaders(context);
            HonuaTelemetry.SetSuccess(activity, batchRequest.Requests.Length);

            if (isMultipartRequest)
            {
                var boundary = $"batchresponse_{Guid.NewGuid():N}";
                var payload = CreateMultipartBatchResponsePayload(
                    response,
                    boundary,
                    ODataProtocolHeaders.GetResponseVersion(context));
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
            HonuaTelemetry.RecordException(activity, ex);
            Log.BatchFailed(_logger, ex);
            return ODataUtilityService.CreateODataError(context, "InternalServerError",
                "An error occurred processing the batch request", 500);
        }
        finally
        {
            activity?.Dispose();
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

        if (!IsJsonContentType(request.ContentType))
        {
            return (null, false, "Batch request content type must be application/json or multipart/mixed.");
        }

        try
        {
            var requestModel = await request.ReadFromJsonAsync<ODataBatchRequest>(
                ODataJsonContext.Default.ODataBatchRequest,
                cancellationToken);

            if (requestModel != null &&
                !TryValidateBatchOperationCount(requestModel.Requests.Length, out var operationLimitError))
            {
                return (null, false, operationLimitError);
            }

            return (requestModel, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
            when (ex is JsonException or NotSupportedException or InvalidOperationException or BadHttpRequestException)
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

    private static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) ||
            !MediaTypeHeaderValue.TryParse(contentType, out var mediaType) ||
            !mediaType.MediaType.HasValue)
        {
            return false;
        }

        var mediaTypeValue = mediaType.MediaType.Value;
        return string.Equals(mediaTypeValue, "application/json", StringComparison.OrdinalIgnoreCase) ||
               mediaTypeValue.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<(ODataBatchRequest? Request, string? Error)> ParseMultipartBatchRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        try
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
                    if (!TryValidateBatchOperationCount(requests.Count + 1, out var operationLimitError))
                    {
                        return (null, operationLimitError);
                    }

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

                var changeset = await ParseChangesetSectionAsync(
                    section,
                    groupId,
                    requestSequence,
                    requests.Count,
                    cancellationToken);
                if (changeset.Requests == null)
                {
                    return (null, changeset.Error);
                }

                requests.AddRange(changeset.Requests);
                requestSequence = changeset.NextRequestSequence;
            }

            return (new ODataBatchRequest { Requests = requests.ToImmutableArray() }, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Log.BatchParseFailed(_logger, ex);
            return (null, "Failed to parse multipart batch request body.");
        }
    }

    private async Task<(List<ODataBatchRequestItem>? Requests, int NextRequestSequence, string? Error)> ParseChangesetSectionAsync(
        MultipartSection section,
        string atomicityGroup,
        int requestSequence,
        int existingRequestCount,
        CancellationToken cancellationToken)
    {
        try
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

                if (!TryValidateBatchOperationCount(existingRequestCount + requests.Count + 1, out var operationLimitError))
                {
                    return (null, requestSequence, operationLimitError);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Log.BatchParseFailed(_logger, ex);
            return (null, requestSequence, "Failed to parse multipart changeset body.");
        }
    }

    private static async Task<(ODataBatchRequestItem? Request, string? Error)> ParseApplicationHttpSectionAsync(
        MultipartSection section,
        string? atomicityGroup,
        string fallbackRequestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var (payload, payloadError) = await ReadSectionPayloadAsync(section, cancellationToken);
        if (payload == null)
        {
            return (null, payloadError);
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

    private static bool TryValidateBatchOperationCount(int requestCount, out string? error)
    {
        if (requestCount > MaxBatchOperationCount)
        {
            error = BatchTooManyOperationsMessage;
            return false;
        }

        error = null;
        return true;
    }

    private static async Task<(string? Payload, string? Error)> ReadSectionPayloadAsync(
        MultipartSection section,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var totalBytesRead = 0L;

        await using var memoryStream = new MemoryStream();
        int bytesRead;
        while ((bytesRead = await section.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            totalBytesRead += bytesRead;
            if (totalBytesRead > MaxApplicationHttpSectionBytes)
            {
                return (null, BatchOperationPayloadTooLargeMessage);
            }

            await memoryStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        if (memoryStream.Length == 0)
        {
            return (null, "application/http section payload is empty.");
        }

        memoryStream.Position = 0;
        using var reader = new StreamReader(memoryStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var payload = await reader.ReadToEndAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(payload)
            ? (null, "application/http section payload is empty.")
            : (payload, null);
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
        return url.Trim().TrimStart('/');
    }

    private static string CreateMultipartBatchResponsePayload(
        ODataBatchResponse response,
        string boundary,
        string odataVersion)
    {
        var builder = new StringBuilder();

        foreach (var item in response.Responses)
        {
            builder.Append("--").Append(boundary).Append("\r\n");
            builder.Append("Content-Type: application/http\r\n");
            builder.Append("Content-Transfer-Encoding: binary\r\n");
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                builder.Append("Content-ID: ").Append(item.Id).Append("\r\n");
            }

            builder.Append("\r\n");
            builder.Append("HTTP/1.1 ")
                .Append(item.Status.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(ReasonPhrases.GetReasonPhrase(item.Status))
                .Append("\r\n");

            builder.Append("OData-Version: ").Append(odataVersion).Append("\r\n");
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

    private async Task PublishBatchFeatureEventsAsync(
        HttpContext context,
        ODataBatchRequest batchRequest,
        ODataBatchResponse response,
        CancellationToken cancellationToken)
    {
        var requestsById = batchRequest.Requests
            .GroupBy(static request => request.Id, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        foreach (var responseItem in response.Responses)
        {
            if (responseItem.Status < 200 || responseItem.Status >= 300)
            {
                continue;
            }

            if (!requestsById.TryGetValue(responseItem.Id, out var request) ||
                !IsMutationMethod(request.Method))
            {
                continue;
            }

            if (!TryResolveLayerId(request, out var layerId) ||
                !TryResolveObjectId(request, responseItem, out var objectId))
            {
                continue;
            }

            var eventOperation = request.Method.ToUpperInvariant() switch
            {
                "POST" => "create",
                "DELETE" => "delete",
                _ => "update"
            };
            await _batchDependencies.MutationEventService.PublishAsync(
                context,
                layerId,
                objectId,
                eventOperation,
                Honua.ServiceDefaults.HonuaTelemetry.Protocols.OData,
                CancellationToken.None,
                mutationFeature: responseItem.MutationFeature,
                serviceProtocol: ODataProtocolConstants.ProtocolName,
                requestId: $"{context.TraceIdentifier}:{responseItem.Id}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Invalidates cache for layers modified in the batch operation
    /// </summary>
    private async Task InvalidateCacheForBatchAsync(
        HttpContext context,
        ODataBatchRequest batchRequest,
        ODataBatchResponse response,
        CancellationToken cancellationToken)
    {
        var cacheInvalidator = context.RequestServices.GetService<OutputCacheInvalidationService>();
        if (cacheInvalidator != null)
        {
            var requestsById = batchRequest.Requests
                .GroupBy(static request => request.Id, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

            var mutatedLayers = new HashSet<int>();
            var hasSuccessfulMutationWithoutLayerId = false;

            foreach (var responseItem in response.Responses)
            {
                if (responseItem.Status < 200 || responseItem.Status >= 300)
                {
                    continue;
                }

                if (!requestsById.TryGetValue(responseItem.Id, out var request) ||
                    !IsMutationMethod(request.Method))
                {
                    continue;
                }

                if (TryResolveLayerId(request, out var layerId))
                {
                    mutatedLayers.Add(layerId);
                    continue;
                }

                hasSuccessfulMutationWithoutLayerId = true;
            }

            if (mutatedLayers.Count > 0)
            {
                foreach (var layerId in mutatedLayers)
                {
                    await _batchDependencies.MutationEventService.InvalidateLayerAsync(null, layerId, cancellationToken);
                }
            }
            else if (hasSuccessfulMutationWithoutLayerId)
            {
                await cacheInvalidator.InvalidateOgcMetadataAsync(cancellationToken);
            }
        }
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

        if (!ODataPathParser.TryParse(NormalizeBatchUrl(request.Url), out var parsed, out _))
        {
            if (request.Body == null ||
                !ODataFeaturePayloadParser.TryParse(request.Body, out var fallbackPayload, out _) ||
                !fallbackPayload.LayerId.HasValue)
            {
                return false;
            }

            layerId = fallbackPayload.LayerId.Value;
            return true;
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

    private static bool TryResolveObjectId(
        ODataBatchRequestItem request,
        ODataBatchResponseItem response,
        out long objectId)
    {
        objectId = 0;
        var method = request.Method.ToUpperInvariant();
        if (method is "PATCH" or "DELETE" or "PUT")
        {
            if (!ODataPathParser.TryParse(NormalizeBatchUrl(request.Url), out var parsed, out _) ||
                !parsed.ObjectId.HasValue)
            {
                return false;
            }

            objectId = parsed.ObjectId.Value;
            return true;
        }

        if (method != "POST")
        {
            return false;
        }

        if (TryGetObjectIdFromBody(response.Body, out objectId))
        {
            return true;
        }

        if (response.Headers != null)
        {
            if ((response.Headers.TryGetValue("EntityId", out var entityId) ||
                 response.Headers.TryGetValue("OData-EntityId", out entityId)) &&
                TryGetObjectIdFromLocation(entityId, out objectId))
            {
                return true;
            }

            if (response.Headers.TryGetValue("Location", out var location) &&
                TryGetObjectIdFromLocation(location, out objectId))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetObjectIdFromBody(object? body, out long objectId)
    {
        objectId = 0;
        if (body is Dictionary<string, object?> dictionary)
        {
            if (!dictionary.TryGetValue("ObjectId", out var raw) || raw == null)
            {
                return false;
            }

            return long.TryParse(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out objectId);
        }

        if (body is JsonElement element &&
            element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("ObjectId", out var property))
        {
            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out objectId))
            {
                return true;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return long.TryParse(
                    property.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out objectId);
            }
        }

        return false;
    }

    private static bool TryGetObjectIdFromLocation(string? location, out long objectId)
    {
        objectId = 0;
        if (string.IsNullOrWhiteSpace(location))
        {
            return false;
        }

        const string key = "ObjectId=";
        var index = location.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = index + key.Length;
        var end = start;
        while (end < location.Length && char.IsDigit(location[end]))
        {
            end++;
        }

        if (end <= start)
        {
            return false;
        }

        return long.TryParse(
            location[start..end],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out objectId);
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

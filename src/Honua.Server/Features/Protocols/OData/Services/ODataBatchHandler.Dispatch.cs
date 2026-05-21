// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Per-request dispatch for the OData $batch handler: routes a single sub-request to the
/// appropriate handler (query, CRUD, streaming) and builds the per-request HttpContext.
/// Also hosts the URL normalization, path parsing, access validation, and collection
/// query-option parsing helpers that the dispatcher and atomic group rely on.
/// </summary>
internal sealed partial class ODataBatchHandler
{
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
}

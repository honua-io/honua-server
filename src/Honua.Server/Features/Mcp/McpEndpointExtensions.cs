// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Maps the single JSON-RPC endpoint that hosts the MCP operator surface.
/// Accepts both individual requests and JSON-RPC 2.0 batches, and enforces
/// the MCP request-id rules (string or integer only). Note that
/// <c>initialize</c> is single-request-only per MCP 2025-03-26 lifecycle —
/// any batch containing an <c>initialize</c> element is rejected wholesale
/// because the server cannot negotiate a protocol version mid-batch.
/// </summary>
internal static class McpEndpointExtensions
{
    /// <summary>
    /// Route for the MCP operator surface.
    /// </summary>
    public const string RoutePath = "/mcp";

    private const string JsonMimeType = "application/json";

    /// <summary>
    /// HTTP status code returned for accepted JSON-RPC notifications. The MCP
    /// HTTP transport spec requires a 202 Accepted with no body when the input
    /// is a notification or response (no <c>id</c>) and the server accepted it.
    /// </summary>
    private const int NotificationAcceptedStatusCode = StatusCodes.Status202Accepted;

    /// <summary>
    /// Explicit JSON <c>null</c> element used as the response <c>id</c> when the
    /// request could not be parsed. JSON-RPC 2.0 requires the id field on error
    /// responses even when the server cannot determine the client's original id,
    /// in which case it MUST be <c>null</c>. Assigning this element to
    /// <see cref="McpJsonRpcResponse.Id"/> prevents the
    /// <c>WhenWritingNull</c> ignore condition from dropping the property.
    /// </summary>
    private static readonly JsonElement JsonNullId = CreateJsonNullElement();

    /// <summary>
    /// Maps <c>POST /mcp</c> for JSON-RPC dispatch.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpOperatorSurface(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(RoutePath,
                static (HttpContext context, CancellationToken ct) => HandleAsync(context, ct))
            .WithDisplayName("MCP Operator Surface")
            .WithName("McpOperatorSurface")
            .WithSummary("MCP JSON-RPC dispatcher for planning, execution, lifecycle, and results.")
            .WithDescription("Accepts JSON-RPC 2.0 requests. Single-request-only: initialize (MCP lifecycle forbids batching). Single or batched: notifications/initialized, tools/list, tools/call, resources/list, resources/templates/list, and resources/read.")
            .WithTags("Mcp");

        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var surface = context.RequestServices.GetRequiredService<McpOperatorSurface>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpOperatorSurface>>();

        JsonDocument? document;
        try
        {
            document = await JsonDocument
                .ParseAsync(context.Request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            McpLog.RequestParseFailed(logger, ex.Message);
            await WriteSingleAsync(
                context,
                ErrorResponse(
                    JsonNullId,
                    McpErrorMapper.ParseError($"Request body is not valid JSON: {ex.Message}")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                await HandleBatchAsync(context, surface, root, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                await WriteSingleAsync(
                    context,
                    ErrorResponse(
                        JsonNullId,
                        McpErrorMapper.InvalidRequest(
                            "Request body must be a JSON-RPC 2.0 request object or batch array.")),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = await ProcessMessageAsync(context, surface, root, cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                context.Response.StatusCode = NotificationAcceptedStatusCode;
                return;
            }

            await WriteSingleAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task HandleBatchAsync(
        HttpContext context,
        McpOperatorSurface surface,
        JsonElement batch,
        CancellationToken cancellationToken)
    {
        // JSON-RPC 2.0 §6 specifies that an empty array is itself an invalid
        // request and must be answered with a single error response.
        if (batch.GetArrayLength() == 0)
        {
            await WriteSingleAsync(
                context,
                ErrorResponse(JsonNullId, McpErrorMapper.InvalidRequest("Batch must contain at least one request.")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // MCP 2025-03-26 lifecycle: the `initialize` request MUST NOT be part
        // of a JSON-RPC batch because the server cannot negotiate a protocol
        // version mid-batch. Reject the entire batch with a single
        // invalid_request envelope whose id is null — the batch as a whole is
        // malformed, so echoing any individual element's id would be
        // misleading.
        if (BatchContainsInitialize(batch))
        {
            await WriteSingleAsync(
                context,
                ErrorResponse(
                    JsonNullId,
                    McpErrorMapper.InvalidRequest(
                        "The initialize request MUST NOT be part of a JSON-RPC batch per MCP 2025-03-26.")),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var responses = new List<McpJsonRpcResponse>();
        foreach (var element in batch.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                responses.Add(ErrorResponse(
                    JsonNullId,
                    McpErrorMapper.InvalidRequest("Batch element must be a JSON-RPC request object.")));
                continue;
            }

            var response = await ProcessMessageAsync(context, surface, element, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                responses.Add(response);
            }
        }

        // All elements were notifications → transport-level 202 Accepted with no body.
        if (responses.Count == 0)
        {
            context.Response.StatusCode = NotificationAcceptedStatusCode;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonMimeType;
        await JsonSerializer
            .SerializeAsync(
                context.Response.Body,
                responses,
                McpJsonContext.Default.ListMcpJsonRpcResponse,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a single JSON-RPC envelope, classifies its id, and dispatches
    /// it through the operator surface. Returns <c>null</c> when the element is
    /// a notification and no response must be emitted.
    /// </summary>
    private static async Task<McpJsonRpcResponse?> ProcessMessageAsync(
        HttpContext context,
        McpOperatorSurface surface,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var idState = ClassifyRequestId(message);
        if (idState.Kind == RequestIdKind.Invalid)
        {
            // MCP 2025-03-26 / JSON-RPC 2.0: id must be a string or integer when
            // present. Null, boolean, fractional number, array, and object ids
            // are rejected — the server responds with id: null because it cannot
            // safely echo a value the client chose incorrectly.
            return ErrorResponse(
                JsonNullId,
                McpErrorMapper.InvalidRequest(
                    "Request id must be a string or integer when present (null, boolean, fractional, array, and object ids are not allowed)."));
        }

        McpJsonRpcRequest? request;
        try
        {
            request = message.Deserialize(McpJsonContext.Default.McpJsonRpcRequest);
        }
        catch (JsonException ex)
        {
            return idState.Kind == RequestIdKind.Notification
                ? null
                : ErrorResponse(
                    idState.Element ?? JsonNullId,
                    McpErrorMapper.InvalidRequest($"Request envelope is not a valid JSON-RPC message: {ex.Message}"));
        }

        if (request is null)
        {
            return idState.Kind == RequestIdKind.Notification
                ? null
                : ErrorResponse(
                    idState.Element ?? JsonNullId,
                    McpErrorMapper.InvalidRequest("Request envelope could not be deserialized."));
        }

        // Normalize request.Id so the dispatcher treats absent/notification the
        // same way regardless of how JsonElement? surfaced missing fields.
        request.Id = idState.Kind == RequestIdKind.Notification ? null : idState.Element;

        return await surface.DispatchAsync(context, request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSingleAsync(
        HttpContext context,
        McpJsonRpcResponse response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = JsonMimeType;
        await JsonSerializer
            .SerializeAsync(context.Response.Body, response, McpJsonContext.Default.McpJsonRpcResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private static McpJsonRpcResponse ErrorResponse(JsonElement id, McpJsonRpcError error) => new()
    {
        Id = id,
        Error = error
    };

    /// <summary>
    /// Returns <c>true</c> when any element of the batch carries the
    /// <c>initialize</c> method. MCP 2025-03-26 forbids batching
    /// <c>initialize</c> because the server cannot negotiate a protocol
    /// version mid-batch, so the whole batch must be rejected before any
    /// element is dispatched.
    /// </summary>
    private static bool BatchContainsInitialize(JsonElement batch)
    {
        foreach (var element in batch.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (element.TryGetProperty("method", out var methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                && string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies the <c>id</c> property of a JSON-RPC message per MCP
    /// 2025-03-26 rules: absent = notification, string or integer = valid,
    /// everything else (null, boolean, fractional number, array, object) is
    /// invalid and must be rejected with an <c>invalid_request</c> error.
    /// </summary>
    private static RequestIdState ClassifyRequestId(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElement))
        {
            return new RequestIdState(RequestIdKind.Notification, null);
        }

        switch (idElement.ValueKind)
        {
            case JsonValueKind.String:
                return new RequestIdState(RequestIdKind.Valid, idElement.Clone());

            case JsonValueKind.Number:
                // MCP restricts numeric ids to integers to match the JSON-RPC 2.0
                // recommendation that ids not contain fractional parts. Validate
                // by token syntax so integers outside Int64 (which clients MAY
                // legally send per JSON's numeric model) are still accepted and
                // echoed back verbatim via the preserved JsonElement.
                return IsIntegerNumberToken(idElement)
                    ? new RequestIdState(RequestIdKind.Valid, idElement.Clone())
                    : new RequestIdState(RequestIdKind.Invalid, null);

            default:
                // Null, Boolean, Array, Object — all disallowed for request ids.
                return new RequestIdState(RequestIdKind.Invalid, null);
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the supplied numeric JSON element is a plain
    /// integer literal (no fractional or exponent part). Used to accept any
    /// JSON-integer id — including values outside <see cref="long"/> range —
    /// without coercing to a CLR numeric type, since the id is preserved and
    /// echoed back as the original <see cref="JsonElement"/>.
    /// </summary>
    private static bool IsIntegerNumberToken(JsonElement element)
    {
        var raw = element.GetRawText().AsSpan();
        if (raw.Length == 0)
        {
            return false;
        }

        var start = raw[0] == '-' ? 1 : 0;
        if (start >= raw.Length)
        {
            return false;
        }

        for (var i = start; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '.' || c == 'e' || c == 'E')
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct RequestIdState(RequestIdKind Kind, JsonElement? Element);

    private enum RequestIdKind
    {
        Notification,
        Valid,
        Invalid
    }

    private static JsonElement CreateJsonNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

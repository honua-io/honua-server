// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Microsoft.Net.Http.Headers;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Maps the JSON-RPC endpoint that hosts the MCP operator surface using the
/// Streamable-HTTP transport (MCP 2025-03-26). Accepts both individual requests
/// and JSON-RPC 2.0 batches on <c>POST /mcp</c>, enforces the MCP request-id
/// rules (string or integer only), and manages <c>Mcp-Session-Id</c> sessions:
/// a session id is issued on <c>initialize</c> and validated (HTTP 404 on an
/// unknown id) on every subsequent request. The transport negotiates the
/// response content type from the client's <c>Accept</c> header — emitting a
/// single Server-Sent-Events <c>message</c> frame when the client accepts
/// <c>text/event-stream</c>, and plain JSON otherwise. <c>GET /mcp</c> opens an
/// SSE stream the server can push notifications over, and <c>DELETE /mcp</c>
/// terminates a session.
/// Note that <c>initialize</c> is single-request-only per MCP 2025-03-26
/// lifecycle — any batch containing an <c>initialize</c> element is rejected
/// wholesale because the server cannot negotiate a protocol version mid-batch.
/// </summary>
internal static class McpEndpointExtensions
{
    /// <summary>
    /// Route for the MCP operator surface.
    /// </summary>
    public const string RoutePath = "/mcp";

    private const string JsonMimeType = "application/json";
    private const string EventStreamMimeType = "text/event-stream";

    /// <summary>
    /// HTTP status code returned for accepted JSON-RPC notifications. The MCP
    /// HTTP transport spec requires a 202 Accepted with no body when the input
    /// is a notification or response (no <c>id</c>) and the server accepted it.
    /// </summary>
    private const int NotificationAcceptedStatusCode = StatusCodes.Status202Accepted;

    /// <summary>
    /// Explicit JSON <c>null</c> element used as the response <c>id</c> when the
    /// request could not be parsed or is malformed. JSON-RPC 2.0 requires the id
    /// field on error responses even when the server cannot determine the
    /// client's original id, in which case it MUST be <c>null</c>. Assigning this
    /// element to <see cref="McpJsonRpcResponse.Id"/> prevents the
    /// <c>WhenWritingNull</c> ignore condition from dropping the property.
    /// Exposed <c>internal</c> so <see cref="McpOperatorSurface"/> can emit
    /// invalid_request error envelopes for malformed requests that reach the
    /// dispatcher without a valid id.
    /// </summary>
    internal static readonly JsonElement JsonNullId = CreateJsonNullElement();

    /// <summary>
    /// Maps <c>POST /mcp</c> for JSON-RPC dispatch, <c>GET /mcp</c> for the
    /// server-to-client SSE stream, and <c>DELETE /mcp</c> for session
    /// termination.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpOperatorSurface(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost(RoutePath,
                static (HttpContext context, CancellationToken ct) => HandlePostAsync(context, ct))
            .WithDisplayName("MCP Operator Surface")
            .WithName("McpOperatorSurface")
            .WithSummary("MCP JSON-RPC dispatcher for planning, execution, lifecycle, and results.")
            .WithDescription("Accepts JSON-RPC 2.0 requests over the Streamable-HTTP transport. Issues an Mcp-Session-Id on initialize and validates it on subsequent requests. Responds with application/json or, when the client accepts text/event-stream, a single SSE message frame. Single-request-only: initialize (MCP lifecycle forbids batching). Single or batched: notifications/initialized, tools/list, tools/call, resources/list, resources/templates/list, resources/read, prompts/list, and prompts/get.")
            .WithTags("Mcp");

        endpoints.MapGet(RoutePath,
                static (HttpContext context, CancellationToken ct) => HandleGetAsync(context, ct))
            .WithDisplayName("MCP Operator Surface (SSE stream)")
            .WithName("McpOperatorSurfaceStream")
            .WithSummary("Opens the MCP server-to-client Server-Sent-Events stream.")
            .WithDescription("Streamable-HTTP transport GET endpoint. Opens a text/event-stream the server uses to push notifications (e.g. progress, listChanged). Requires a valid Mcp-Session-Id.")
            .WithTags("Mcp");

        endpoints.MapDelete(RoutePath,
                static (HttpContext context, CancellationToken ct) => HandleDeleteAsync(context, ct))
            .WithDisplayName("MCP Operator Surface (session termination)")
            .WithName("McpOperatorSurfaceTerminate")
            .WithSummary("Terminates an MCP session.")
            .WithDescription("Streamable-HTTP transport DELETE endpoint. Removes the session identified by the Mcp-Session-Id header.")
            .WithTags("Mcp");

        return endpoints;
    }

    private static async Task HandlePostAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var surface = context.RequestServices.GetRequiredService<McpOperatorSurface>();
        var sessions = context.RequestServices.GetRequiredService<McpSessionManager>();
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
            var isInitialize = IsInitialize(root);

            // Streamable-HTTP session enforcement: when the client presents an
            // Mcp-Session-Id it MUST be one this server issued, otherwise the
            // spec requires HTTP 404 so the client knows to re-initialize. The
            // initialize request itself never carries a (validated) session id —
            // it is how a session is established — so it bypasses validation.
            if (!isInitialize
                && context.Request.Headers.TryGetValue(McpSessionManager.SessionHeaderName, out var presented)
                && !string.IsNullOrEmpty(presented.ToString())
                && !sessions.IsValid(presented.ToString()))
            {
                McpLog.SessionRejected(logger, "unknown-or-expired");
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

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

            // A successful initialize establishes a session. Issue the id on the
            // Mcp-Session-Id response header so the client echoes it on every
            // subsequent request.
            if (isInitialize && response.Error is null)
            {
                var sessionId = sessions.CreateSession();
                context.Response.Headers[McpSessionManager.SessionHeaderName] = sessionId;
                McpLog.SessionIssued(logger, sessionId);
            }

            await WriteSingleAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles <c>GET /mcp</c>: opens the server-to-client SSE stream defined by
    /// the Streamable-HTTP transport. A valid <c>Mcp-Session-Id</c> is required;
    /// the client must also accept <c>text/event-stream</c>. The stream stays
    /// open (emitting no events in this increment — server-initiated progress and
    /// listChanged notifications are a follow-up) until the client disconnects.
    /// </summary>
    private static async Task HandleGetAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var sessions = context.RequestServices.GetRequiredService<McpSessionManager>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpOperatorSurface>>();

        if (!AcceptsEventStream(context))
        {
            // The transport reserves GET for the SSE channel; a client that does
            // not accept text/event-stream cannot use it.
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var sessionId = context.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        if (string.IsNullOrEmpty(sessionId) || !sessions.IsValid(sessionId))
        {
            McpLog.SessionRejected(logger, "stream-requires-valid-session");
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        StartEventStream(context);
        // Flush the response head so the client (and proxies) see the open SSE
        // stream immediately, before any server-initiated frame is available.
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Hold the stream open for server-initiated messages. No frames are
        // pushed yet (progress/listChanged land in a follow-up); the stream
        // closes when the client cancels or disconnects.
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal stream teardown.
        }
    }

    /// <summary>
    /// Handles <c>DELETE /mcp</c>: terminates the session named by the
    /// <c>Mcp-Session-Id</c> header. Returns 204 when a session was removed and
    /// 404 when the id is unknown, per the Streamable-HTTP transport.
    /// </summary>
    private static Task HandleDeleteAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var sessions = context.RequestServices.GetRequiredService<McpSessionManager>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpOperatorSurface>>();

        var sessionId = context.Request.Headers[McpSessionManager.SessionHeaderName].ToString();
        if (string.IsNullOrEmpty(sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        var found = sessions.Terminate(sessionId);
        McpLog.SessionTerminated(logger, sessionId, found);
        context.Response.StatusCode = found
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status404NotFound;
        return Task.CompletedTask;
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

        await WriteBatchAsync(context, responses, cancellationToken).ConfigureAwait(false);
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
            // A malformed envelope (for example, wrong field types) is never a
            // valid MCP notification. Returning HTTP 202 with no body would
            // mask the client-side bug; JSON-RPC 2.0 allows id: null on error
            // responses when the server cannot determine the client's id, so
            // surface invalid_request regardless of whether the broken message
            // carried an id field.
            return ErrorResponse(
                idState.Element ?? JsonNullId,
                McpErrorMapper.InvalidRequest($"Request envelope is not a valid JSON-RPC message: {ex.Message}"));
        }

        if (request is null)
        {
            return ErrorResponse(
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

        if (AcceptsEventStream(context))
        {
            // Streamable-HTTP: when the client accepts text/event-stream the
            // server MAY answer a POST with an SSE stream. We emit the single
            // JSON-RPC response as one `message` event and close the stream.
            var payload = JsonSerializer.Serialize(response, McpJsonContext.Default.McpJsonRpcResponse);
            StartEventStream(context);
            await WriteEventAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = JsonMimeType;
        await JsonSerializer
            .SerializeAsync(context.Response.Body, response, McpJsonContext.Default.McpJsonRpcResponse, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteBatchAsync(
        HttpContext context,
        List<McpJsonRpcResponse> responses,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;

        if (AcceptsEventStream(context))
        {
            var payload = JsonSerializer.Serialize(responses, McpJsonContext.Default.ListMcpJsonRpcResponse);
            StartEventStream(context);
            await WriteEventAsync(context, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

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
    /// Returns <c>true</c> when the request's <c>Accept</c> header advertises
    /// <c>text/event-stream</c> (exactly or via a <c>*/*</c> / <c>text/*</c>
    /// wildcard), meaning the server may answer with a Server-Sent-Events stream.
    /// </summary>
    private static bool AcceptsEventStream(HttpContext context)
    {
        var accept = context.Request.Headers.Accept;
        if (accept.Count == 0)
        {
            return false;
        }

        foreach (var value in accept)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            foreach (var media in value.Split(','))
            {
                // Strip any q-value/parameters after ';' and trim whitespace.
                var token = media.Split(';', 2)[0].Trim();
                if (token.Equals(EventStreamMimeType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void StartEventStream(HttpContext context)
    {
        context.Response.ContentType = EventStreamMimeType;
        // SSE responses must not be cached or buffered along the path.
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache";
        context.Response.Headers[HeaderNames.Connection] = "keep-alive";
    }

    /// <summary>
    /// Writes a single SSE <c>message</c> event whose <c>data:</c> field carries
    /// the JSON-RPC payload, terminated by the blank line the SSE wire format
    /// requires, then flushes so the client receives the frame promptly.
    /// </summary>
    private static async Task WriteEventAsync(HttpContext context, string json, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("event: message\n");
        // A JSON payload never contains a raw newline at the top level after
        // serialization, so a single data: line is sufficient; the blank line
        // delimits the event per the SSE specification.
        builder.Append("data: ").Append(json).Append("\n\n");

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static McpJsonRpcResponse ErrorResponse(JsonElement id, McpJsonRpcError error) => new()
    {
        Id = id,
        Error = error
    };

    /// <summary>
    /// Returns <c>true</c> when the supplied JSON element is a single JSON-RPC
    /// object whose <c>method</c> is <c>initialize</c>. Used by the transport to
    /// decide whether to bypass session validation and whether to issue a new
    /// session id on success.
    /// </summary>
    private static bool IsInitialize(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("method", out var methodElement)
        && methodElement.ValueKind == JsonValueKind.String
        && string.Equals(methodElement.GetString(), "initialize", StringComparison.Ordinal);

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
            if (IsInitialize(element))
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

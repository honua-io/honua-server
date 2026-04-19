// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Mcp.Models;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Maps the single JSON-RPC endpoint that hosts the MCP operator surface.
/// Clients POST one MCP request per HTTP request; batch framing is deferred.
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
            .WithDescription("Accepts JSON-RPC 2.0 requests for initialize, notifications/initialized, tools/list, tools/call, resources/list, and resources/read.")
            .WithTags("Mcp");

        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var surface = context.RequestServices.GetRequiredService<McpOperatorSurface>();
        var logger = context.RequestServices.GetRequiredService<ILogger<McpOperatorSurface>>();

        McpJsonRpcRequest? request;
        try
        {
            request = await JsonSerializer
                .DeserializeAsync(context.Request.Body, McpJsonContext.Default.McpJsonRpcRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            McpLog.RequestParseFailed(logger, ex.Message);
            await WriteResponseAsync(
                context,
                new McpJsonRpcResponse
                {
                    Id = JsonNullId,
                    Error = McpErrorMapper.InvalidArgument($"Request body is not valid JSON-RPC: {ex.Message}")
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteResponseAsync(
                context,
                new McpJsonRpcResponse
                {
                    Id = JsonNullId,
                    Error = McpErrorMapper.InvalidArgument("Request body is required.")
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = await surface
            .DispatchAsync(context, request, cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            // Notification (no id) or notifications/* method — JSON-RPC 2.0
            // forbids a response body, so reply 202 Accepted with no payload.
            context.Response.StatusCode = NotificationAcceptedStatusCode;
            return;
        }

        await WriteResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteResponseAsync(
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

    private static JsonElement CreateJsonNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}

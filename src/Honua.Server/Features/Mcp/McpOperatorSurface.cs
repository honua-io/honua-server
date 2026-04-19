// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Mcp.Models;
using Honua.Server.Features.Mcp.Resources;
using Honua.Server.Features.Mcp.Tools;

namespace Honua.Server.Features.Mcp;

/// <summary>
/// Central JSON-RPC dispatcher and registry for the MCP operator surface.
/// Hosts the tool and resource catalogs, routes <c>initialize</c>,
/// <c>notifications/initialized</c>, <c>tools/list</c>, <c>tools/call</c>,
/// <c>resources/list</c>, and <c>resources/read</c> methods, and converts
/// domain exceptions into JSON-RPC errors via <see cref="McpErrorMapper"/>.
/// </summary>
internal sealed class McpOperatorSurface
{
    /// <summary>
    /// MCP protocol revisions this server can negotiate during <c>initialize</c>.
    /// Listed newest-first so the latest entry is returned when the client asks
    /// for an unsupported revision.
    /// See https://modelcontextprotocol.io/docs/learn/versioning.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = ["2025-03-26"];

    /// <summary>
    /// Latest MCP protocol revision supported by this server. Used when the
    /// client requests a version we do not implement.
    /// </summary>
    public static string LatestProtocolVersion => SupportedProtocolVersions[0];

    private readonly IReadOnlyDictionary<string, IMcpTool> _tools;
    private readonly IReadOnlyList<IMcpResource> _resources;
    private readonly ILogger<McpOperatorSurface> _logger;

    public McpOperatorSurface(
        IEnumerable<IMcpTool> tools,
        IEnumerable<IMcpResource> resources,
        ILogger<McpOperatorSurface> logger)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _resources = resources.ToList();
        _logger = logger;

        McpLog.SurfaceInitialized(_logger, _tools.Count, _resources.Sum(r => r.Describe().Count));
    }

    public IReadOnlyCollection<string> ToolNames => (IReadOnlyCollection<string>)_tools.Keys;

    public IReadOnlyList<IMcpResource> Resources => _resources;

    /// <summary>
    /// Dispatches a JSON-RPC request and returns a response envelope, or
    /// <c>null</c> when the input is a JSON-RPC notification (no <c>id</c> or a
    /// <c>notifications/*</c> method) and therefore must not receive a reply
    /// per the JSON-RPC 2.0 and MCP HTTP transport rules. Uses
    /// <see cref="McpErrorMapper"/> to translate domain exceptions into JSON-RPC
    /// error objects; other unhandled exceptions surface as <c>internal</c>.
    /// </summary>
    public async Task<McpJsonRpcResponse?> DispatchAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var isNotification = IsNotification(request);

        if (!string.Equals(request.JsonRpc, "2.0", StringComparison.Ordinal))
        {
            return isNotification
                ? null
                : ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("jsonrpc must be \"2.0\"."));
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            return isNotification
                ? null
                : ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("method is required."));
        }

        // MCP lifecycle notifications carry no response per JSON-RPC 2.0.
        // `notifications/initialized` is required after a successful initialize;
        // unknown notifications are dropped silently rather than erroring so
        // forward-compatible clients can layer in new notification types.
        if (request.Method.StartsWith("notifications/", StringComparison.Ordinal))
        {
            return null;
        }

        if (isNotification)
        {
            // A non-notifications method without an id is malformed JSON-RPC.
            // Per spec we cannot respond (no id), so drop silently.
            return null;
        }

        try
        {
            return request.Method switch
            {
                "initialize" => HandleInitialize(request),
                "tools/list" => SuccessResponse(request.Id, ListTools(), McpJsonContext.Default.McpToolsListResult),
                "tools/call" => await CallToolAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                "resources/list" => SuccessResponse(request.Id, ListResources(), McpJsonContext.Default.McpResourcesListResult),
                "resources/read" => await ReadResourceAsync(httpContext, request, cancellationToken).ConfigureAwait(false),
                _ => ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP method '{request.Method}'."))
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ErrorResponse(request.Id, McpErrorMapper.Map(ex));
        }
    }

    /// <summary>
    /// JSON-RPC 2.0 distinguishes notifications from requests by the absence
    /// of the <c>id</c> member. <see cref="JsonElement"/>? is <c>null</c> when
    /// the field was missing on the wire.
    /// </summary>
    private static bool IsNotification(McpJsonRpcRequest request) => request.Id is null;

    private McpJsonRpcResponse HandleInitialize(McpJsonRpcRequest request)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpInitializeParams);
        if (parameters is null)
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument(
                    "initialize requires params with protocolVersion, capabilities, and clientInfo."));
        }

        if (string.IsNullOrWhiteSpace(parameters.ProtocolVersion))
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.protocolVersion is required."));
        }

        if (parameters.Capabilities is null
            || parameters.Capabilities.Value.ValueKind != JsonValueKind.Object)
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.capabilities must be an object."));
        }

        if (parameters.ClientInfo is null || string.IsNullOrWhiteSpace(parameters.ClientInfo.Name))
        {
            return ErrorResponse(
                request.Id,
                McpErrorMapper.InvalidArgument("initialize.params.clientInfo.name is required."));
        }

        var negotiatedVersion = NegotiateProtocolVersion(parameters.ProtocolVersion);
        var result = new McpInitializeResult { ProtocolVersion = negotiatedVersion };
        return SuccessResponse(request.Id, result, McpJsonContext.Default.McpInitializeResult);
    }

    private static string NegotiateProtocolVersion(string requestedVersion)
    {
        for (var i = 0; i < SupportedProtocolVersions.Count; i++)
        {
            if (string.Equals(SupportedProtocolVersions[i], requestedVersion, StringComparison.Ordinal))
            {
                return requestedVersion;
            }
        }

        return LatestProtocolVersion;
    }

    private McpToolsListResult ListTools() => new()
    {
        Tools = _tools.Values
            .Select(t => t.Describe())
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList()
    };

    private async Task<McpJsonRpcResponse> CallToolAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpToolsCallParams);
        if (string.IsNullOrWhiteSpace(parameters?.Name))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("tools/call requires a tool name."));
        }

        if (!_tools.TryGetValue(parameters.Name, out var tool))
        {
            return ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP tool '{parameters.Name}'."));
        }

        try
        {
            var result = await tool
                .InvokeAsync(httpContext, parameters.Arguments, cancellationToken)
                .ConfigureAwait(false);
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Ok),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolCompleted(_logger, tool.Name, McpTelemetry.Status.Ok);
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpToolsCallResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = McpErrorMapper.Map(ex);
            McpTelemetry.ToolCallCount.Add(
                1,
                new KeyValuePair<string, object?>("tool_name", tool.Name),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error),
                new KeyValuePair<string, object?>("workflow_family", tool.WorkflowFamily));
            McpLog.ToolFailed(_logger, tool.Name, error.Data?.Code ?? McpErrorMapper.Codes.Internal, error.Message);
            return ErrorResponse(request.Id, error);
        }
    }

    private McpResourcesListResult ListResources() => new()
    {
        Resources = _resources
            .SelectMany(r => r.Describe())
            .OrderBy(d => d.Uri, StringComparer.Ordinal)
            .ToList()
    };

    private async Task<McpJsonRpcResponse> ReadResourceAsync(
        HttpContext httpContext,
        McpJsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        var parameters = ParseParams(request.Params, McpJsonContext.Default.McpResourcesReadParams);
        if (string.IsNullOrWhiteSpace(parameters?.Uri))
        {
            return ErrorResponse(request.Id, McpErrorMapper.InvalidArgument("resources/read requires a URI."));
        }

        var handler = _resources.FirstOrDefault(r => r.CanHandle(parameters.Uri));
        if (handler is null)
        {
            return ErrorResponse(request.Id, McpErrorMapper.NotFound($"Unknown MCP resource '{parameters.Uri}'."));
        }

        try
        {
            var result = await handler
                .ReadAsync(httpContext, parameters.Uri, cancellationToken)
                .ConfigureAwait(false);
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", handler.Family),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Ok));
            return SuccessResponse(request.Id, result, McpJsonContext.Default.McpResourcesReadResult);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = McpErrorMapper.Map(ex);
            McpTelemetry.ResourceReadCount.Add(
                1,
                new KeyValuePair<string, object?>("resource_family", handler.Family),
                new KeyValuePair<string, object?>("status", McpTelemetry.Status.Error));
            return ErrorResponse(request.Id, error);
        }
    }

    private static T? ParseParams<T>(JsonElement? parameters, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (parameters is null || parameters.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        try
        {
            return parameters.Value.Deserialize(typeInfo);
        }
        catch (JsonException ex)
        {
            throw new GeoprocessingValidationException(
                $"JSON-RPC params are not valid: {ex.Message}");
        }
    }

    private static McpJsonRpcResponse SuccessResponse<T>(
        JsonElement? id,
        T payload,
        JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        using var document = JsonDocument.Parse(json);
        return new McpJsonRpcResponse
        {
            Id = id,
            Result = document.RootElement.Clone()
        };
    }

    private static McpJsonRpcResponse ErrorResponse(JsonElement? id, McpJsonRpcError error) => new()
    {
        Id = id,
        Error = error
    };
}

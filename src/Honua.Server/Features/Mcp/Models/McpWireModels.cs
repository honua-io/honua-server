// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Mcp.Models;

/// <summary>
/// JSON-RPC 2.0 request envelope used by the MCP wire protocol.
/// </summary>
internal sealed class McpJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; set; }

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 response envelope for successful invocations.
/// </summary>
internal sealed class McpJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("id")]
    public JsonElement? Id { get; set; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public McpJsonRpcError? Error { get; set; }
}

/// <summary>
/// JSON-RPC 2.0 error object. The <see cref="Code"/> uses MCP/JSON-RPC numeric
/// codes; <see cref="Data"/> carries the structured envelope from
/// <see cref="McpErrorMapper"/> including the string error code and hints.
/// </summary>
internal sealed class McpJsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public McpErrorData? Data { get; set; }
}

/// <summary>
/// Structured error data attached to <see cref="McpJsonRpcError.Data"/>.
/// Mirrors the canonical error envelope so clients can handle recoverable
/// conditions (approval, idempotency, auth) without parsing message strings.
/// </summary>
internal sealed class McpErrorData
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("requiresReauthentication")]
    public bool? RequiresReauthentication { get; set; }

    [JsonPropertyName("approvalRequired")]
    public bool? ApprovalRequired { get; set; }

    [JsonPropertyName("policyRef")]
    public string? PolicyRef { get; set; }

    [JsonPropertyName("conflictingJobId")]
    public string? ConflictingJobId { get; set; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation>? Violations { get; set; }
}

/// <summary>
/// Machine-readable validation failure attached to <see cref="McpErrorData.Violations"/>.
/// </summary>
internal sealed class McpValidationViolation
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("fieldPath")]
    public string? FieldPath { get; set; }
}

// -----------------------------------------------------------------------
// MCP protocol payloads
// -----------------------------------------------------------------------

/// <summary>
/// Request payload for the MCP <c>initialize</c> method. The MCP lifecycle
/// requires the client to advertise its protocol version, capabilities, and
/// client info so the server can negotiate a compatible session.
/// See https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle.
/// </summary>
internal sealed class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    public McpClientInfo? ClientInfo { get; set; }
}

/// <summary>
/// Identifies the calling MCP client during <c>initialize</c>.
/// </summary>
internal sealed class McpClientInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

/// <summary>
/// Response payload for the MCP <c>initialize</c> method.
/// </summary>
internal sealed class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();
}

internal sealed class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public McpCapabilityFlag Tools { get; set; } = new();

    [JsonPropertyName("resources")]
    public McpCapabilityFlag Resources { get; set; } = new();
}

internal sealed class McpCapabilityFlag
{
    [JsonPropertyName("listChanged")]
    public bool ListChanged { get; set; }
}

internal sealed class McpServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "honua.operator.mcp";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "v1";
}

/// <summary>
/// Response payload for <c>tools/list</c>.
/// </summary>
internal sealed class McpToolsListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDescriptor> Tools { get; set; } = [];
}

internal sealed class McpToolDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }
}

/// <summary>
/// Request payload for <c>tools/call</c>.
/// </summary>
internal sealed class McpToolsCallParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }
}

/// <summary>
/// Response payload for <c>tools/call</c>.
/// </summary>
internal sealed class McpToolsCallResult
{
    [JsonPropertyName("content")]
    public IReadOnlyList<McpContentBlock> Content { get; set; } = [];

    [JsonPropertyName("isError")]
    public bool IsError { get; set; }

    [JsonPropertyName("structuredContent")]
    public JsonElement? StructuredContent { get; set; }
}

internal sealed class McpContentBlock
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

/// <summary>
/// Response payload for <c>resources/list</c>.
/// </summary>
internal sealed class McpResourcesListResult
{
    [JsonPropertyName("resources")]
    public IReadOnlyList<McpResourceDescriptor> Resources { get; set; } = [];
}

internal sealed class McpResourceDescriptor
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "application/json";
}

/// <summary>
/// Request payload for <c>resources/read</c>.
/// </summary>
internal sealed class McpResourcesReadParams
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }
}

/// <summary>
/// Response payload for <c>resources/read</c>.
/// </summary>
internal sealed class McpResourcesReadResult
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<McpResourceContent> Contents { get; set; } = [];
}

internal sealed class McpResourceContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "application/json";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

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

internal sealed class McpJsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public McpErrorData? Data { get; set; }
}

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

    [JsonPropertyName("studioAuthorizationCode")]
    public string? StudioAuthorizationCode { get; set; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; set; }

    [JsonPropertyName("resourceUri")]
    public string? ResourceUri { get; set; }

    [JsonPropertyName("conflictingJobId")]
    public string? ConflictingJobId { get; set; }

    [JsonPropertyName("currentGeneration")]
    public long? CurrentGeneration { get; set; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; set; }

    [JsonPropertyName("retryAfterSeconds")]
    public int? RetryAfterSeconds { get; set; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    // The missing dependency is named so an agent can stop retrying a capability the install cannot provide.
    [JsonPropertyName("missingDependency")]
    public string? MissingDependency { get; set; }

    [JsonPropertyName("capability")]
    public string? Capability { get; set; }

    [JsonPropertyName("missingEntitlement")]
    public string? MissingEntitlement { get; set; }

    [JsonPropertyName("remediation")]
    public string? Remediation { get; set; }

    [JsonPropertyName("remediationRef")]
    public string? RemediationRef { get; set; }

    [JsonPropertyName("violations")]
    public IReadOnlyList<McpValidationViolation>? Violations { get; set; }
}

internal sealed class McpValidationViolation
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("fieldPath")]
    public string? FieldPath { get; set; }
}

internal sealed class McpJsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

internal sealed class McpProgressNotificationParams
{
    [JsonPropertyName("progressToken")]
    public string ProgressToken { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("total")]
    public double? Total { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

// -----------------------------------------------------------------------
// MCP protocol payloads
// -----------------------------------------------------------------------

internal sealed class McpInitializeParams
{
    [JsonPropertyName("protocolVersion")]
    public string? ProtocolVersion { get; set; }

    [JsonPropertyName("capabilities")]
    public JsonElement? Capabilities { get; set; }

    [JsonPropertyName("clientInfo")]
    public McpClientInfo? ClientInfo { get; set; }
}

internal sealed class McpClientInfo
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class McpInitializeResult
{
    [JsonPropertyName("protocolVersion")]
    public string ProtocolVersion { get; set; } = string.Empty;

    [JsonPropertyName("capabilities")]
    public McpServerCapabilities Capabilities { get; set; } = new();

    [JsonPropertyName("serverInfo")]
    public McpServerInfo ServerInfo { get; set; } = new();

    [JsonPropertyName("_meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public McpInitializeResultMeta? Meta { get; set; }
}

internal sealed class McpInitializeResultMeta
{
    [JsonPropertyName("honua.io/session-mode")]
    public string SessionMode { get; set; } = string.Empty;
}

internal sealed class McpServerCapabilities
{
    [JsonPropertyName("tools")]
    public McpCapabilityFlag Tools { get; set; } = new();

    [JsonPropertyName("resources")]
    public McpCapabilityFlag Resources { get; set; } = new();

    [JsonPropertyName("prompts")]
    public McpCapabilityFlag Prompts { get; set; } = new();
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

internal sealed class McpListParams
{
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

internal sealed class McpToolsListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDescriptor> Tools { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

internal sealed class McpToolDescriptor
{
    private JsonElement? _outputSchema;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }

    [JsonPropertyName("outputSchema")]
    public JsonElement? OutputSchema
    {
        get => _outputSchema;
        set => _outputSchema = value is null
            ? null
            : Tools.McpToolOutputSchemas.IncludeErrorEnvelope(value.Value);
    }

    // Behavior hints are advisory and must not be trusted for authorization.
    [JsonPropertyName("annotations")]
    public McpToolAnnotations? Annotations { get; set; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; set; }
}

internal sealed class McpToolAnnotations
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("readOnlyHint")]
    public bool? ReadOnlyHint { get; set; }

    [JsonPropertyName("destructiveHint")]
    public bool? DestructiveHint { get; set; }

    [JsonPropertyName("idempotentHint")]
    public bool? IdempotentHint { get; set; }

    [JsonPropertyName("openWorldHint")]
    public bool? OpenWorldHint { get; set; }
}

internal sealed class McpToolsCallParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; set; }

    [JsonPropertyName("_meta")]
    public JsonElement? Meta { get; set; }
}

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

    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class McpResourcesListResult
{
    [JsonPropertyName("resources")]
    public IReadOnlyList<McpResourceDescriptor> Resources { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
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

internal sealed class McpResourceTemplatesListResult
{
    [JsonPropertyName("resourceTemplates")]
    public IReadOnlyList<McpResourceTemplateDescriptor> ResourceTemplates { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

internal sealed class McpResourceTemplateDescriptor
{
    [JsonPropertyName("uriTemplate")]
    public string UriTemplate { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "application/json";
}

internal sealed class McpResourcesReadParams
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    [JsonPropertyName("maxChars")]
    public int? MaxChars { get; set; }
}

internal sealed class McpResourcesReadResult
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<McpResourceContent> Contents { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

internal sealed class McpResourceContent
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "application/json";

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("blob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Blob { get; set; }
}

// -----------------------------------------------------------------------
// MCP prompts (prompts/list, prompts/get)
// -----------------------------------------------------------------------

internal sealed class McpPromptsListResult
{
    [JsonPropertyName("prompts")]
    public IReadOnlyList<McpPromptDescriptor> Prompts { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

internal sealed class McpPromptDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public IReadOnlyList<McpPromptArgument> Arguments { get; set; } = [];
}

internal sealed class McpPromptArgument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

internal sealed class McpPromptsGetParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public Dictionary<string, string>? Arguments { get; set; }
}

internal sealed class McpPromptGetResult
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<McpPromptMessage> Messages { get; set; } = [];
}

internal sealed class McpPromptMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public McpContentBlock Content { get; set; } = new();
}

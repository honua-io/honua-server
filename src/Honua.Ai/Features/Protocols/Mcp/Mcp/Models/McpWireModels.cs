// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Models;

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

/// <summary>
/// JSON-RPC 2.0 notification envelope pushed from the server to the client over
/// the Streamable-HTTP <c>GET /mcp</c> Server-Sent-Events stream (honua-server#1954).
/// A notification carries no <c>id</c> per JSON-RPC 2.0; the client never replies.
/// Used for <c>notifications/progress</c> (long-running job progress) and the
/// <c>notifications/tools/list_changed</c> / <c>notifications/resources/list_changed</c>
/// catalog-change signals.
/// </summary>
internal sealed class McpJsonRpcNotification
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }
}

/// <summary>
/// Parameters for an MCP <c>notifications/progress</c> message. The
/// <see cref="ProgressToken"/> correlates the progress stream with the work the
/// client started; Honua uses the durable job id as the token so a client that
/// received a job id from <c>honua_execute_plan</c> can correlate streamed
/// progress with that job. <see cref="Progress"/>/<see cref="Total"/> express a
/// 0–100 completion ratio sourced from the canonical job runtime's
/// <c>PercentComplete</c>, and <see cref="Message"/> mirrors the job's current
/// phase. See https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/progress.
/// </summary>
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

    /// <summary>
    /// Advertises the MCP <c>prompts</c> capability (<c>prompts/list</c> /
    /// <c>prompts/get</c>). Present so schema-driven clients discover the curated
    /// workflow-prompt catalog during <c>initialize</c>. The Honua catalog is
    /// static, so <c>listChanged</c> is always false.
    /// See https://modelcontextprotocol.io/specification/2025-03-26/server/prompts.
    /// </summary>
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

/// <summary>
/// Request payload shared by the paginated MCP list methods (<c>tools/list</c>,
/// <c>resources/list</c>, <c>resources/templates/list</c>, <c>prompts/list</c>).
/// The optional opaque <c>cursor</c> resumes a previous page; clients MUST echo
/// the <c>nextCursor</c> from the prior result verbatim and treat it as opaque.
/// See https://modelcontextprotocol.io/specification/2025-03-26/server/utilities/pagination.
/// </summary>
internal sealed class McpListParams
{
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>
/// Response payload for <c>tools/list</c>.
/// </summary>
internal sealed class McpToolsListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDescriptor> Tools { get; set; } = [];

    /// <summary>
    /// Opaque cursor for the next page, present only when more tools remain.
    /// Omitted (null) on the final page per MCP 2025-03-26 pagination.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

internal sealed class McpToolDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable display title for the tool. Per the MCP 2025-03-26 tool
    /// annotations shape this is surfaced both as a top-level <c>title</c> and on
    /// <see cref="Annotations"/>; a client may prefer either. Null tools omit it.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public JsonElement InputSchema { get; set; }

    /// <summary>
    /// JSON-schema document describing the tool's structured result, published
    /// under the standard MCP <c>outputSchema</c> tool field (added in the
    /// 2025-06-18 revision this server now negotiates, honua-server#1954). It
    /// describes the same <see cref="McpToolsCallResult.StructuredContent"/>
    /// payload. Null for tools whose result carries no structured content (e.g.
    /// image-only renders). Clients negotiating the older 2025-03-26 revision
    /// ignore this field harmlessly.
    /// </summary>
    [JsonPropertyName("outputSchema")]
    public JsonElement? OutputSchema { get; set; }

    /// <summary>
    /// MCP 2025-03-26 tool behavior hints. Lets a client LLM distinguish
    /// read-only planning/grounding tools from write tools and reason about
    /// destructiveness/idempotency before invoking. Hints are advisory and must
    /// not be trusted for security; the server still enforces authorization.
    /// </summary>
    [JsonPropertyName("annotations")]
    public McpToolAnnotations? Annotations { get; set; }
}

/// <summary>
/// MCP 2025-03-26 tool annotation hints
/// (<c>annotations: { title, readOnlyHint, destructiveHint, idempotentHint,
/// openWorldHint }</c>). Each hint is advisory metadata describing the tool's
/// behavior so a client LLM can plan tool use; the server does not rely on them
/// for authorization. See
/// https://modelcontextprotocol.io/specification/2025-03-26/server/tools#tool-annotations.
/// </summary>
internal sealed class McpToolAnnotations
{
    /// <summary>Human-readable display title for the tool.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// <c>true</c> when the tool does not modify its environment (planning,
    /// grounding, validation, preview, read queries). When true,
    /// <see cref="DestructiveHint"/> and <see cref="IdempotentHint"/> carry no
    /// meaning and are omitted.
    /// </summary>
    [JsonPropertyName("readOnlyHint")]
    public bool? ReadOnlyHint { get; set; }

    /// <summary>
    /// <c>true</c> when a write tool may perform destructive updates (e.g.
    /// cancelling an in-flight job). Only meaningful when
    /// <see cref="ReadOnlyHint"/> is false; omitted for read-only tools.
    /// </summary>
    [JsonPropertyName("destructiveHint")]
    public bool? DestructiveHint { get; set; }

    /// <summary>
    /// <c>true</c> when repeated calls with the same arguments have no
    /// additional effect (e.g. execute honors an idempotency key; cancel is a
    /// no-op once requested). Only meaningful when <see cref="ReadOnlyHint"/> is
    /// false; omitted for read-only tools.
    /// </summary>
    [JsonPropertyName("idempotentHint")]
    public bool? IdempotentHint { get; set; }

    /// <summary>
    /// <c>true</c> when the tool interacts with an open world of external
    /// entities (e.g. third-party geocoding/routing providers). Omitted when the
    /// tool operates only over the server's closed catalog.
    /// </summary>
    [JsonPropertyName("openWorldHint")]
    public bool? OpenWorldHint { get; set; }
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

    /// <summary>
    /// Base64-encoded binary payload for an <c>image</c> content block. Per the
    /// MCP 2025-03-26 content-block schema an image block carries
    /// <c>{ "type": "image", "data": "&lt;base64&gt;", "mimeType": "image/png" }</c>.
    /// Null for text blocks (omitted from the wire by the
    /// <c>WhenWritingNull</c> serializer policy) so existing text tools are
    /// unaffected.
    /// </summary>
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    /// <summary>
    /// MIME type for an <c>image</c> or <c>resource_link</c> content block (e.g.
    /// <c>image/png</c>). Null for text blocks.
    /// </summary>
    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    /// <summary>
    /// Target URI for a <c>resource_link</c> content block (MCP 2025-06-18). Used
    /// by <c>honua_render_map</c> to hand back a fetchable artifact href instead
    /// of inlining a multi-megabyte base64 image into the model context. Null for
    /// text/image blocks (omitted from the wire by <c>WhenWritingNull</c>).
    /// </summary>
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Human-readable name for a <c>resource_link</c> content block. Null for
    /// text/image blocks.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>
/// Response payload for <c>resources/list</c>. Only concrete resource URIs appear
/// here; parameterized URIs belong on <c>resources/templates/list</c>.
/// </summary>
internal sealed class McpResourcesListResult
{
    [JsonPropertyName("resources")]
    public IReadOnlyList<McpResourceDescriptor> Resources { get; set; } = [];

    /// <summary>
    /// Opaque cursor for the next page, present only when more resources remain.
    /// Omitted (null) on the final page per MCP 2025-03-26 pagination.
    /// </summary>
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

/// <summary>
/// Response payload for <c>resources/templates/list</c>. MCP 2025-03-26 requires
/// parameterized resource URIs (e.g. <c>honua://jobs/{jobId}</c>) to be advertised
/// with an <c>uriTemplate</c> field on this surface rather than mixed into
/// <c>resources/list</c>.
/// </summary>
internal sealed class McpResourceTemplatesListResult
{
    [JsonPropertyName("resourceTemplates")]
    public IReadOnlyList<McpResourceTemplateDescriptor> ResourceTemplates { get; set; } = [];

    /// <summary>
    /// Opaque cursor for the next page, present only when more templates remain.
    /// Omitted (null) on the final page per MCP 2025-03-26 pagination.
    /// </summary>
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

/// <summary>
/// Request payload for <c>resources/read</c>.
/// </summary>
internal sealed class McpResourcesReadParams
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    /// <summary>
    /// Optional opaque cursor that resumes a chunked read of a large resource
    /// document (job results, catalogs). Honua extension over MCP 2025-03-26,
    /// which does not paginate <c>resources/read</c>; clients echo the prior
    /// <c>nextCursor</c> verbatim. Omit to read from the start of the document.
    /// </summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }
}

/// <summary>
/// Response payload for <c>resources/read</c>.
/// </summary>
internal sealed class McpResourcesReadResult
{
    [JsonPropertyName("contents")]
    public IReadOnlyList<McpResourceContent> Contents { get; set; } = [];

    /// <summary>
    /// Opaque cursor for the next chunk of a large resource document, present
    /// only when more content remains. Honua extension over MCP 2025-03-26; each
    /// page's <c>text</c> concatenates per <c>uri</c> to rebuild the full
    /// document. Omitted (null) when the whole document was returned.
    /// </summary>
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
    public string Text { get; set; } = string.Empty;
}

// -----------------------------------------------------------------------
// MCP prompts (prompts/list, prompts/get)
// -----------------------------------------------------------------------

/// <summary>
/// Response payload for <c>prompts/list</c>. Advertises the curated catalog of
/// reusable geospatial workflow prompts (site selection, hazard assessment,
/// permit review, dashboard scaffolding) so a client LLM can offer them as
/// first-class entrypoints. See
/// https://modelcontextprotocol.io/specification/2025-03-26/server/prompts.
/// </summary>
internal sealed class McpPromptsListResult
{
    [JsonPropertyName("prompts")]
    public IReadOnlyList<McpPromptDescriptor> Prompts { get; set; } = [];

    /// <summary>
    /// Opaque cursor for the next page, present only when more prompts remain.
    /// Omitted (null) on the final page per MCP 2025-03-26 pagination.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }
}

/// <summary>
/// Describes a single prompt in <c>prompts/list</c>: its stable name, a
/// human-readable display title and description, and the templated arguments a
/// client substitutes before <c>prompts/get</c>.
/// </summary>
internal sealed class McpPromptDescriptor
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human-readable display title for the prompt.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public IReadOnlyList<McpPromptArgument> Arguments { get; set; } = [];
}

/// <summary>
/// A templated argument a prompt accepts. <see cref="Required"/> marks whether a
/// client must supply the value before <c>prompts/get</c> can render the prompt.
/// </summary>
internal sealed class McpPromptArgument
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public bool Required { get; set; }
}

/// <summary>
/// Request payload for <c>prompts/get</c>: the prompt name and the
/// argument-value map the server substitutes into the prompt template.
/// </summary>
internal sealed class McpPromptsGetParams
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public Dictionary<string, string>? Arguments { get; set; }
}

/// <summary>
/// Response payload for <c>prompts/get</c>: the rendered prompt as an ordered
/// list of conversation messages plus an optional description.
/// </summary>
internal sealed class McpPromptGetResult
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<McpPromptMessage> Messages { get; set; } = [];
}

/// <summary>
/// A single message in a rendered prompt. Mirrors the MCP 2025-03-26 prompt
/// message shape: a <c>role</c> (<c>user</c> | <c>assistant</c>) and a single
/// content block.
/// </summary>
internal sealed class McpPromptMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public McpContentBlock Content { get; set; } = new();
}

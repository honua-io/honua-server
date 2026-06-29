// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// Arguments for <c>honua_resolve_entity</c> (#1949): a natural-language name or
/// phrase the client LLM wants to resolve to concrete catalog entity references
/// (services and layers) it can then pass to <c>honua_query_features</c> /
/// <c>honua_render_map</c>.
/// </summary>
internal sealed class McpResolveEntityArgument
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

/// <summary>
/// Structured result of <c>honua_resolve_entity</c>: the ranked entity references
/// the supplied text resolved to, grounded in the live Metadata v2 catalog.
/// </summary>
internal sealed class McpResolveEntityOutput
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = "any";

    [JsonPropertyName("matchCount")]
    public int MatchCount { get; set; }

    [JsonPropertyName("matches")]
    public IReadOnlyList<McpEntityMatch> Matches { get; set; } = [];
}

/// <summary>
/// A single resolved catalog entity reference returned by
/// <c>honua_resolve_entity</c>. <see cref="ServiceId"/> plus <see cref="LayerId"/>
/// (for a layer match) are the actionable references the client passes to the
/// query/render tools.
/// </summary>
internal sealed class McpEntityMatch
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "layer";

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("layerId")]
    public int? LayerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }
}

/// <summary>
/// Arguments for <c>honua_list_capabilities</c> (#1949). All optional: a cold
/// client LLM can call it with no arguments to discover the full server surface.
/// </summary>
internal sealed class McpListCapabilitiesArgument
{
    [JsonPropertyName("includeResources")]
    public bool? IncludeResources { get; set; }

    [JsonPropertyName("includeGroundingResources")]
    public bool? IncludeGroundingResources { get; set; }
}

/// <summary>
/// Structured result of <c>honua_list_capabilities</c>: a self-describing manifest
/// of the tools, resources, and grounding resources the live <c>/mcp</c> surface
/// exposes, so a client LLM with no Honua knowledge can plan a workflow. Sourced
/// from the same in-process catalog served over both HTTP-SSE and stdio (#1950),
/// so the manifest is transport-symmetric.
/// </summary>
internal sealed class McpListCapabilitiesOutput
{
    [JsonPropertyName("serverName")]
    public string ServerName { get; set; } = string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;

    [JsonPropertyName("protocolVersions")]
    public IReadOnlyList<string> ProtocolVersions { get; set; } = [];

    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<McpCapabilityTool> Tools { get; set; } = [];

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }

    [JsonPropertyName("resources")]
    public IReadOnlyList<McpCapabilityResource> Resources { get; set; } = [];

    [JsonPropertyName("groundingResources")]
    public IReadOnlyList<string> GroundingResources { get; set; } = [];
}

/// <summary>
/// A tool entry in the capability manifest: the LLM-grade name, title,
/// description, read/write classification, and workflow family.
/// </summary>
internal sealed class McpCapabilityTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("readOnly")]
    public bool ReadOnly { get; set; }

    [JsonPropertyName("workflowFamily")]
    public string WorkflowFamily { get; set; } = string.Empty;
}

/// <summary>
/// A resource entry in the capability manifest: the URI (or URI template), name,
/// description, and telemetry family.
/// </summary>
internal sealed class McpCapabilityResource
{
    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;
}

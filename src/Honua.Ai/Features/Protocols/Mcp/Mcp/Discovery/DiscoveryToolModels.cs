// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.Protocols.Mcp.Discovery;

internal sealed class McpResolveEntityArgument
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("entityType")]
    public string? EntityType { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

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

internal sealed class McpEntityMatch
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "layer";

    [JsonPropertyName("serviceId")]
    public string ServiceId { get; set; } = string.Empty;

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("serviceLayerIndex")]
    public int? ServiceLayerIndex { get; set; }

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

internal sealed class McpListCapabilitiesArgument
{
    [JsonPropertyName("includeResources")]
    public bool? IncludeResources { get; set; }

    [JsonPropertyName("includeGroundingResources")]
    public bool? IncludeGroundingResources { get; set; }

    [JsonPropertyName("toolCursor")]
    public string? ToolCursor { get; set; }

    [JsonPropertyName("resourceCursor")]
    public string? ResourceCursor { get; set; }

    [JsonPropertyName("fullExport")]
    public bool? FullExport { get; set; }
}

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

    [JsonPropertyName("totalToolCount")]
    public int TotalToolCount { get; set; }

    [JsonPropertyName("nextToolCursor")]
    public string? NextToolCursor { get; set; }

    [JsonPropertyName("tools")]
    public IReadOnlyList<McpCapabilityTool> Tools { get; set; } = [];

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; set; }

    [JsonPropertyName("totalResourceCount")]
    public int TotalResourceCount { get; set; }

    [JsonPropertyName("nextResourceCursor")]
    public string? NextResourceCursor { get; set; }

    [JsonPropertyName("resources")]
    public IReadOnlyList<McpCapabilityResource> Resources { get; set; } = [];

    [JsonPropertyName("groundingResources")]
    public IReadOnlyList<string> GroundingResources { get; set; } = [];

    [JsonPropertyName("workflowViews")]
    public IReadOnlyList<Views.McpWorkflowViewSummary> WorkflowViews { get; set; } = [];
}

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

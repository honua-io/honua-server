// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Views;

internal sealed class McpWorkflowViewToolsListResult
{
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDescriptor> Tools { get; set; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("_meta")]
    public McpWorkflowViewMeta? Meta { get; set; }
}

internal sealed class McpWorkflowViewMeta
{
    [JsonPropertyName("view")]
    public string View { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("revisionDigest")]
    public string RevisionDigest { get; set; } = string.Empty;

    [JsonPropertyName("membershipDigest")]
    public string MembershipDigest { get; set; } = string.Empty;

    [JsonPropertyName("descriptorDigest")]
    public string DescriptorDigest { get; set; } = string.Empty;

    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    [JsonPropertyName("descriptorBytes")]
    public int DescriptorBytes { get; set; }

    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("fullCatalogView")]
    public string FullCatalogView { get; set; } = McpWorkflowViewNegotiation.FullCatalogViewName;

    [JsonPropertyName("stages")]
    public IReadOnlyList<McpWorkflowViewStageMeta> Stages { get; set; } = [];
}

internal sealed class McpWorkflowViewStageMeta
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("tools")]
    public IReadOnlyList<string> Tools { get; set; } = [];
}

internal sealed class McpWorkflowViewSummary
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    [JsonPropertyName("revisionDigest")]
    public string RevisionDigest { get; set; } = string.Empty;

    [JsonPropertyName("membershipDigest")]
    public string MembershipDigest { get; set; } = string.Empty;

    [JsonPropertyName("descriptorDigest")]
    public string DescriptorDigest { get; set; } = string.Empty;

    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    [JsonPropertyName("descriptorBytes")]
    public int DescriptorBytes { get; set; }

    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("stages")]
    public IReadOnlyList<string> Stages { get; set; } = [];
}

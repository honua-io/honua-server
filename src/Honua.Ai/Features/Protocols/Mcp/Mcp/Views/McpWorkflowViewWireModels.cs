// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// <c>tools/list</c> result served when a workflow view is selected. It is the
/// standard MCP result shape plus an MCP <c>_meta</c> block naming the view and
/// its deterministic digests, so a client can prove which bounded surface it
/// received and detect a membership change without diffing schemas.
/// </summary>
/// <remarks>
/// A view is budget-bounded by construction, so the whole view is served in one
/// page and <c>nextCursor</c> is always absent. The complete paginated catalog
/// remains available by selecting no view.
/// </remarks>
internal sealed class McpWorkflowViewToolsListResult
{
    /// <summary>The view's members, in deterministic wire order.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<McpToolDescriptor> Tools { get; set; } = [];

    /// <summary>
    /// Always <c>null</c>: a view fits in one page by budget. Present so the wire
    /// shape stays identical to an unnarrowed <c>tools/list</c> result.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    /// <summary>The view identity, digests, and measured budget numbers.</summary>
    [JsonPropertyName("_meta")]
    public McpWorkflowViewMeta? Meta { get; set; }
}

/// <summary>
/// The <c>_meta</c> block a view-narrowed <c>tools/list</c> carries.
/// </summary>
internal sealed class McpWorkflowViewMeta
{
    /// <summary>The selected view name.</summary>
    [JsonPropertyName("view")]
    public string View { get; set; } = string.Empty;

    /// <summary>Human/model-readable view title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Server-authored revision label (for example <c>setup.v1</c>).</summary>
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical view definition (stages and rules).</summary>
    [JsonPropertyName("revisionDigest")]
    public string RevisionDigest { get; set; } = string.Empty;

    /// <summary>SHA-256 of the ordered stage/member list.</summary>
    [JsonPropertyName("membershipDigest")]
    public string MembershipDigest { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical serialized descriptor array served here.</summary>
    [JsonPropertyName("descriptorDigest")]
    public string DescriptorDigest { get; set; } = string.Empty;

    /// <summary>Number of descriptors in the view.</summary>
    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    /// <summary>Aggregate canonical descriptor JSON bytes.</summary>
    [JsonPropertyName("descriptorBytes")]
    public int DescriptorBytes { get; set; }

    /// <summary>Estimated model tokens for the aggregate descriptor payload.</summary>
    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    /// <summary>
    /// How to reach the complete paginated catalog: the reserved view name that
    /// opts back out of any negotiated or configured narrowing.
    /// </summary>
    [JsonPropertyName("fullCatalogView")]
    public string FullCatalogView { get; set; } = McpWorkflowViewNegotiation.FullCatalogViewName;

    /// <summary>The view's stages, in journey order.</summary>
    [JsonPropertyName("stages")]
    public IReadOnlyList<McpWorkflowViewStageMeta> Stages { get; set; } = [];
}

/// <summary>One stage of a view as reported on the wire.</summary>
internal sealed class McpWorkflowViewStageMeta
{
    /// <summary>Stable stage identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Human/model-readable stage title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>What the agent accomplishes in this stage.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>The member tool names selected into this stage, in wire order.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<string> Tools { get; set; } = [];
}

/// <summary>
/// A published workflow view as advertised by <c>honua_list_capabilities</c>, so a
/// client discovers views from the server instead of hard-coding a name.
/// </summary>
internal sealed class McpWorkflowViewSummary
{
    /// <summary>The view name to pass as <c>tools/list</c> <c>view</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Human/model-readable title.</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>What journey the view covers and when to select it.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>Server-authored revision label.</summary>
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical view definition.</summary>
    [JsonPropertyName("revisionDigest")]
    public string RevisionDigest { get; set; } = string.Empty;

    /// <summary>SHA-256 of the ordered stage/member list.</summary>
    [JsonPropertyName("membershipDigest")]
    public string MembershipDigest { get; set; } = string.Empty;

    /// <summary>SHA-256 of the canonical serialized descriptor array.</summary>
    [JsonPropertyName("descriptorDigest")]
    public string DescriptorDigest { get; set; } = string.Empty;

    /// <summary>Number of descriptors the view currently publishes.</summary>
    [JsonPropertyName("toolCount")]
    public int ToolCount { get; set; }

    /// <summary>Aggregate canonical descriptor JSON bytes.</summary>
    [JsonPropertyName("descriptorBytes")]
    public int DescriptorBytes { get; set; }

    /// <summary>Estimated model tokens for the aggregate descriptor payload.</summary>
    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    /// <summary>The view's stage ids, in journey order.</summary>
    [JsonPropertyName("stages")]
    public IReadOnlyList<string> Stages { get; set; } = [];
}

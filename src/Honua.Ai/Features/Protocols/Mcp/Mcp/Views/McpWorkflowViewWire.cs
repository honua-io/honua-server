// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// Projects a resolved <see cref="McpWorkflowViewProjection"/> onto the MCP wire
/// shapes: the narrowed <c>tools/list</c> result and the discovery summary
/// <c>honua_list_capabilities</c> advertises.
/// </summary>
internal static class McpWorkflowViewWire
{
    /// <summary>
    /// Builds the narrowed <c>tools/list</c> result. The descriptors are the exact
    /// live ones — never re-described, re-schematized, or truncated.
    /// </summary>
    public static McpWorkflowViewToolsListResult BuildToolsListResult(McpWorkflowViewProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        return new McpWorkflowViewToolsListResult
        {
            Tools = projection.Descriptors,
            NextCursor = null,
            Meta = BuildMeta(projection),
        };
    }

    /// <summary>Builds the <c>_meta</c> block describing the served view.</summary>
    public static McpWorkflowViewMeta BuildMeta(McpWorkflowViewProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        var stages = new List<McpWorkflowViewStageMeta>(projection.Definition.Stages.Count);
        foreach (var stage in projection.Definition.Stages)
        {
            stages.Add(new McpWorkflowViewStageMeta
            {
                Id = stage.Id,
                Title = stage.Title,
                Description = stage.Description,
                Tools = projection.Members
                    .Where(m => string.Equals(m.StageId, stage.Id, StringComparison.Ordinal))
                    .Select(static m => m.ToolName)
                    .ToArray(),
            });
        }

        return new McpWorkflowViewMeta
        {
            View = projection.Definition.Name,
            Title = projection.Definition.Title,
            Revision = projection.Definition.Revision,
            RevisionDigest = projection.RevisionDigest,
            MembershipDigest = projection.MembershipDigest,
            DescriptorDigest = projection.DescriptorDigest,
            ToolCount = projection.Members.Count,
            DescriptorBytes = projection.AggregateCanonicalBytes,
            EstimatedTokens = projection.EstimatedTokens,
            Stages = stages,
        };
    }

    /// <summary>
    /// Builds the discovery summary a client reads to learn which views exist,
    /// so no client keeps its own view or tool inventory.
    /// </summary>
    public static McpWorkflowViewSummary BuildSummary(McpWorkflowViewProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        return new McpWorkflowViewSummary
        {
            Name = projection.Definition.Name,
            Title = projection.Definition.Title,
            Description = projection.Definition.Description,
            Revision = projection.Definition.Revision,
            RevisionDigest = projection.RevisionDigest,
            MembershipDigest = projection.MembershipDigest,
            DescriptorDigest = projection.DescriptorDigest,
            ToolCount = projection.Members.Count,
            DescriptorBytes = projection.AggregateCanonicalBytes,
            EstimatedTokens = projection.EstimatedTokens,
            Stages = projection.Definition.Stages.Select(static s => s.Id).ToArray(),
        };
    }
}

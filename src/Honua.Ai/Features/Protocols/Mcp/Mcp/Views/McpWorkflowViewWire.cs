// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

internal static class McpWorkflowViewWire
{
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

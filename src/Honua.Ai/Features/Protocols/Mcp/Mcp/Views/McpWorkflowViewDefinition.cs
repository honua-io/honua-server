// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

internal enum McpWorkflowViewRuleKind
{
    ExactName,

    NamePrefix,
}

internal sealed record McpWorkflowViewMemberRule(McpWorkflowViewRuleKind Kind, string Value)
{
    public static McpWorkflowViewMemberRule Exact(string name) =>
        new(McpWorkflowViewRuleKind.ExactName, name);

    public static McpWorkflowViewMemberRule Prefix(string prefix) =>
        new(McpWorkflowViewRuleKind.NamePrefix, prefix);

    public bool Matches(string toolName) => Kind switch
    {
        McpWorkflowViewRuleKind.ExactName => string.Equals(Value, toolName, StringComparison.Ordinal),
        McpWorkflowViewRuleKind.NamePrefix => toolName.StartsWith(Value, StringComparison.Ordinal),
        _ => false,
    };

    public string ToCanonicalString() => Kind switch
    {
        McpWorkflowViewRuleKind.ExactName => "exact:" + Value,
        _ => "prefix:" + Value,
    };
}

internal sealed record McpWorkflowViewStageDefinition
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required IReadOnlyList<McpWorkflowViewMemberRule> Rules { get; init; }

    public IReadOnlyList<McpWorkflowViewMemberRule> Exclusions { get; init; } = [];

    public bool Selects(string toolName)
    {
        for (var i = 0; i < Exclusions.Count; i++)
        {
            if (Exclusions[i].Matches(toolName))
            {
                return false;
            }
        }

        for (var i = 0; i < Rules.Count; i++)
        {
            if (Rules[i].Matches(toolName))
            {
                return true;
            }
        }

        return false;
    }
}

// A view only narrows tools/list and never grants or caches authority.
internal sealed record McpWorkflowViewDefinition
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Revision { get; init; }

    public required IReadOnlyList<McpWorkflowViewStageDefinition> Stages { get; init; }

    public int MaxDescriptors { get; init; } = McpWorkflowViewBudget.MaxDescriptors;

    public int MaxAggregateDescriptorBytes { get; init; } = McpWorkflowViewBudget.MaxAggregateDescriptorBytes;

    public int MaxDescriptorBytes { get; init; } = McpWorkflowViewBudget.MaxDescriptorBytes;

    public int FindStageIndex(string toolName)
    {
        for (var i = 0; i < Stages.Count; i++)
        {
            if (Stages[i].Selects(toolName))
            {
                return i;
            }
        }

        return -1;
    }

    public string ToCanonicalString()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("view=").Append(Name).Append('\n');
        builder.Append("revision=").Append(Revision).Append('\n');
        foreach (var stage in Stages)
        {
            builder.Append("stage=").Append(stage.Id).Append('\n');
            foreach (var rule in stage.Rules)
            {
                builder.Append("  include=").Append(rule.ToCanonicalString()).Append('\n');
            }

            foreach (var rule in stage.Exclusions)
            {
                builder.Append("  exclude=").Append(rule.ToCanonicalString()).Append('\n');
            }
        }

        return builder.ToString();
    }
}

internal static class McpWorkflowViewBudget
{
    public const int MaxDescriptors = 48;

    public const int MaxAggregateDescriptorBytes = 128 * 1024;

    public const int MaxDescriptorBytes = 16 * 1024;

    public static int EstimateTokens(int characters) => characters / 4;
}

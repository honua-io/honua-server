// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Views;

// Dynamic tools are ordered last so a publication appends to the tools array instead of re-sorting it and invalidating a prompt cache.
internal sealed record McpWorkflowViewMember(
    string ToolName,
    string StageId,
    int StageIndex,
    bool IsDynamic,
    McpToolDescriptor Descriptor,
    int CanonicalBytes,
    string Digest);

internal sealed record McpWorkflowViewProjection
{
    public required McpWorkflowViewDefinition Definition { get; init; }

    public required IReadOnlyList<McpWorkflowViewMember> Members { get; init; }

    public required IReadOnlyList<string> EmptyStageIds { get; init; }

    public required int AggregateCanonicalBytes { get; init; }

    public required int LargestDescriptorBytes { get; init; }

    public required string RevisionDigest { get; init; }

    public required string MembershipDigest { get; init; }

    public required string DescriptorDigest { get; init; }

    public IReadOnlyList<McpToolDescriptor> Descriptors =>
        Members.Select(static m => m.Descriptor).ToArray();

    public int EstimatedTokens => McpWorkflowViewBudget.EstimateTokens(AggregateCanonicalBytes);

    public IReadOnlyList<string> BudgetViolations
    {
        get
        {
            var violations = new List<string>();
            if (Members.Count > Definition.MaxDescriptors)
            {
                violations.Add(
                    $"view '{Definition.Name}' publishes {Members.Count} descriptors, "
                    + $"over the {Definition.MaxDescriptors}-descriptor budget");
            }

            if (AggregateCanonicalBytes > Definition.MaxAggregateDescriptorBytes)
            {
                violations.Add(
                    $"view '{Definition.Name}' aggregate canonical descriptor JSON is "
                    + $"{AggregateCanonicalBytes} bytes, over the "
                    + $"{Definition.MaxAggregateDescriptorBytes}-byte budget");
            }

            foreach (var member in Members.Where(m => m.CanonicalBytes > Definition.MaxDescriptorBytes))
            {
                violations.Add(
                    $"descriptor '{member.ToolName}' is {member.CanonicalBytes} bytes, over the "
                    + $"{Definition.MaxDescriptorBytes}-byte per-descriptor budget");
            }

            return violations;
        }
    }
}

internal static class McpWorkflowViewProjector
{
    public static McpWorkflowViewProjection Project(
        McpWorkflowViewDefinition definition,
        IEnumerable<(McpToolDescriptor Descriptor, bool IsDynamic)> catalog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalog);

        var selected = new List<McpWorkflowViewMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (descriptor, isDynamic) in catalog)
        {
            if (descriptor.Name is null || !seen.Add(descriptor.Name))
            {
                continue;
            }

            var stageIndex = definition.FindStageIndex(descriptor.Name);
            if (stageIndex < 0)
            {
                continue;
            }

            var canonical = SerializeDescriptor(descriptor);
            selected.Add(new McpWorkflowViewMember(
                descriptor.Name,
                definition.Stages[stageIndex].Id,
                stageIndex,
                isDynamic,
                descriptor,
                Encoding.UTF8.GetByteCount(canonical),
                Digest(canonical)));
        }

        // Static members are deterministic by stage/name. Dynamic members retain
        // source publication order in the tail, so a source that appends a new
        // operation preserves the existing descriptor prefix across refreshes.
        var staticMembers = selected
            .Where(static member => !member.IsDynamic)
            .OrderBy(static member => member.StageIndex)
            .ThenBy(static member => member.ToolName, StringComparer.Ordinal);
        var dynamicMembers = selected.Where(static member => member.IsDynamic);
        selected = staticMembers.Concat(dynamicMembers).ToList();

        var populatedStages = selected.Select(static m => m.StageId).ToHashSet(StringComparer.Ordinal);
        var emptyStages = definition.Stages
            .Where(s => !populatedStages.Contains(s.Id))
            .Select(static s => s.Id)
            .ToArray();

        var descriptors = selected.Select(static m => m.Descriptor).ToArray();
        var aggregate = JsonSerializer.Serialize(
            (IReadOnlyList<McpToolDescriptor>)descriptors,
            McpJsonContext.Default.IReadOnlyListMcpToolDescriptor);

        var membership = new StringBuilder();
        foreach (var member in selected)
        {
            membership.Append(member.StageId).Append('/').Append(member.ToolName).Append('\n');
        }

        return new McpWorkflowViewProjection
        {
            Definition = definition,
            Members = selected,
            EmptyStageIds = emptyStages,
            AggregateCanonicalBytes = Encoding.UTF8.GetByteCount(aggregate),
            LargestDescriptorBytes = selected.Count == 0 ? 0 : selected.Max(static m => m.CanonicalBytes),
            RevisionDigest = Digest(definition.ToCanonicalString()),
            MembershipDigest = Digest(membership.ToString()),
            DescriptorDigest = Digest(aggregate),
        };
    }

    private static string SerializeDescriptor(McpToolDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, McpJsonContext.Default.McpToolDescriptor);

    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

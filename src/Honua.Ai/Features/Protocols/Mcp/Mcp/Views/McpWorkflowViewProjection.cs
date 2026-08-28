// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// One member of a projected workflow view: the exact canonical descriptor the
/// live catalog serves, plus the stage it was selected into and its measured
/// canonical size/digest.
/// </summary>
/// <param name="ToolName">The advertised tool name.</param>
/// <param name="StageId">The stage that selected this member.</param>
/// <param name="StageIndex">The stage's ordinal position in the view.</param>
/// <param name="IsDynamic">
/// Whether the member came from a runtime <see cref="Tools.IMcpToolSource"/>
/// rather than the static catalog. Dynamic members are ordered last so a
/// mid-conversation publication appends to the tools array instead of re-sorting
/// it, which would invalidate a host's prompt cache.
/// </param>
/// <param name="Descriptor">The verbatim live descriptor — never truncated.</param>
/// <param name="CanonicalBytes">UTF-8 byte length of the canonical descriptor JSON.</param>
/// <param name="Digest">SHA-256 digest of the canonical descriptor JSON.</param>
internal sealed record McpWorkflowViewMember(
    string ToolName,
    string StageId,
    int StageIndex,
    bool IsDynamic,
    McpToolDescriptor Descriptor,
    int CanonicalBytes,
    string Digest);

/// <summary>
/// A workflow view resolved against the canonical live catalog: the ordered
/// members, the measured budget numbers, and the deterministic revision,
/// membership, and descriptor digests.
/// </summary>
internal sealed record McpWorkflowViewProjection
{
    /// <summary>The server-authored definition this projection resolved.</summary>
    public required McpWorkflowViewDefinition Definition { get; init; }

    /// <summary>The selected members, in wire order.</summary>
    public required IReadOnlyList<McpWorkflowViewMember> Members { get; init; }

    /// <summary>
    /// Stage ids that selected no live member. A stage is empty when the host did
    /// not compose that capability (for example a server with no renderer), which
    /// is honest absence, not truncation.
    /// </summary>
    public required IReadOnlyList<string> EmptyStageIds { get; init; }

    /// <summary>UTF-8 byte length of the canonical serialized descriptor array.</summary>
    public required int AggregateCanonicalBytes { get; init; }

    /// <summary>Largest single-descriptor canonical byte length in the view.</summary>
    public required int LargestDescriptorBytes { get; init; }

    /// <summary>
    /// SHA-256 of the canonical definition text — changes whenever the stage set,
    /// stage order, or membership rules change.
    /// </summary>
    public required string RevisionDigest { get; init; }

    /// <summary>SHA-256 of the ordered member-name list.</summary>
    public required string MembershipDigest { get; init; }

    /// <summary>SHA-256 of the canonical serialized descriptor array actually served.</summary>
    public required string DescriptorDigest { get; init; }

    /// <summary>The member descriptors, in wire order.</summary>
    public IReadOnlyList<McpToolDescriptor> Descriptors =>
        Members.Select(static m => m.Descriptor).ToArray();

    /// <summary>Estimated model tokens for the aggregate descriptor payload.</summary>
    public int EstimatedTokens => McpWorkflowViewBudget.EstimateTokens(AggregateCanonicalBytes);

    /// <summary>
    /// Budget violations, one human-readable message each. Empty when the view is
    /// within every ceiling. The budget is enforced by a test gate rather than by
    /// dropping members at runtime, so an over-budget view is a build-time signal
    /// to split or refine server-owned stages.
    /// </summary>
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

/// <summary>
/// Derives a <see cref="McpWorkflowViewProjection"/> from a server-authored
/// definition and the canonical live catalog (honua-server#3428).
/// </summary>
/// <remarks>
/// This is the whole derivation seam: there is no hand-maintained membership list
/// and no copied schema. The projector reads the live descriptors, applies the
/// definition's stage rules, orders the result deterministically, and measures it.
/// </remarks>
internal static class McpWorkflowViewProjector
{
    /// <summary>
    /// Projects <paramref name="definition"/> over the live catalog entries.
    /// </summary>
    /// <param name="definition">The server-authored view.</param>
    /// <param name="catalog">
    /// The canonical live catalog: each entry is a descriptor exactly as
    /// <c>tools/list</c> would serve it, flagged with whether it came from a
    /// runtime tool source.
    /// </param>
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

        // Deterministic wire order. Static members first, grouped by stage in the
        // server-authored journey order, then by ordinal name; runtime-published
        // members follow in the same shape. Keeping dynamic members in the tail
        // means a mid-conversation `tools/list_changed` refresh APPENDS to the
        // tools array rather than re-sorting it, which is what preserves a host's
        // prompt cache (MCP 2026-07-28 client best practices, caching note).
        selected.Sort(static (a, b) =>
        {
            var byDynamic = a.IsDynamic.CompareTo(b.IsDynamic);
            if (byDynamic != 0)
            {
                return byDynamic;
            }

            var byStage = a.StageIndex.CompareTo(b.StageIndex);
            return byStage != 0 ? byStage : string.CompareOrdinal(a.ToolName, b.ToolName);
        });

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

    /// <summary>
    /// Canonical serialization of a single descriptor — the exact wire form,
    /// schemas included and never truncated.
    /// </summary>
    private static string SerializeDescriptor(McpToolDescriptor descriptor) =>
        JsonSerializer.Serialize(descriptor, McpJsonContext.Default.McpToolDescriptor);

    /// <summary>Lowercase hex SHA-256 digest, prefixed with the algorithm.</summary>
    private static string Digest(string value) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

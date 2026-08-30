// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// How one <see cref="McpWorkflowViewStageDefinition"/> rule matches a live tool
/// name. Rules match the <em>advertised</em> tool name the canonical catalog
/// serves, never a copied description or schema — the descriptor itself is always
/// taken verbatim from the live catalog (honua-server#3428).
/// </summary>
internal enum McpWorkflowViewRuleKind
{
    /// <summary>Matches one exact advertised tool name.</summary>
    ExactName,

    /// <summary>
    /// Matches every advertised tool name starting with the rule value. Family
    /// prefixes are what make a view self-updating: a newly registered or
    /// runtime-published operation in a covered family joins the view with no
    /// edit to this definition and no client-side source list.
    /// </summary>
    NamePrefix,
}

/// <summary>
/// One membership rule of a workflow-view stage.
/// </summary>
/// <param name="Kind">How the rule matches.</param>
/// <param name="Value">The exact name or the family prefix.</param>
internal sealed record McpWorkflowViewMemberRule(McpWorkflowViewRuleKind Kind, string Value)
{
    /// <summary>Builds an exact-name rule.</summary>
    public static McpWorkflowViewMemberRule Exact(string name) =>
        new(McpWorkflowViewRuleKind.ExactName, name);

    /// <summary>Builds a family-prefix rule.</summary>
    public static McpWorkflowViewMemberRule Prefix(string prefix) =>
        new(McpWorkflowViewRuleKind.NamePrefix, prefix);

    /// <summary>Whether this rule selects <paramref name="toolName"/>.</summary>
    public bool Matches(string toolName) => Kind switch
    {
        McpWorkflowViewRuleKind.ExactName => string.Equals(Value, toolName, StringComparison.Ordinal),
        McpWorkflowViewRuleKind.NamePrefix => toolName.StartsWith(Value, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>
    /// Canonical text form of the rule, used to derive the deterministic view
    /// revision digest.
    /// </summary>
    public string ToCanonicalString() => Kind switch
    {
        McpWorkflowViewRuleKind.ExactName => "exact:" + Value,
        _ => "prefix:" + Value,
    };
}

/// <summary>
/// One server-authored stage of a workflow view: a named step of the bounded
/// journey plus the rules that select its members from the canonical live
/// catalog.
/// </summary>
internal sealed record McpWorkflowViewStageDefinition
{
    /// <summary>Stable stage identifier (for example <c>readiness</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human/model-readable stage title.</summary>
    public required string Title { get; init; }

    /// <summary>What the agent is trying to accomplish in this stage.</summary>
    public required string Description { get; init; }

    /// <summary>Rules selecting this stage's members.</summary>
    public required IReadOnlyList<McpWorkflowViewMemberRule> Rules { get; init; }

    /// <summary>
    /// Rules removing a name a broader family rule would otherwise claim (for
    /// example keeping the publication-submit tool out of the composition stage).
    /// </summary>
    public IReadOnlyList<McpWorkflowViewMemberRule> Exclusions { get; init; } = [];

    /// <summary>Whether this stage selects <paramref name="toolName"/>.</summary>
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

/// <summary>
/// A named, server-authored discovery view over the canonical live MCP catalog
/// (honua-server#3428). A view is <b>discovery only</b>: it narrows which
/// descriptors <c>tools/list</c> returns and never grants, caches, or implies
/// authority. Every <c>tools/call</c> continues to reauthenticate and
/// reauthorize against the current canonical actor, tenant, roles/grants, OAuth
/// scope, and policy on the existing call-time path.
/// </summary>
/// <remarks>
/// <para>
/// A definition owns only the <em>selection</em> (stage names, ordering, and
/// membership rules). Descriptions, annotations, and input/output schemas are
/// always taken verbatim from the live catalog descriptor, so there is no second
/// hand-maintained name/schema inventory anywhere — on the server or in a client.
/// </para>
/// <para>
/// Budgets are part of the definition and are enforced by a test gate rather than
/// by silently dropping members: if the bounded path outgrows a ceiling, the
/// server-owned stages are split or refined, never truncated.
/// </para>
/// </remarks>
internal sealed record McpWorkflowViewDefinition
{
    /// <summary>The view name a client selects (for example <c>setup</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Human/model-readable view title.</summary>
    public required string Title { get; init; }

    /// <summary>What journey the view covers and when to select it.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Server-authored revision label. Bumped whenever the stage set or the
    /// membership rules change; the machine-checkable counterpart is the
    /// revision digest derived from <see cref="ToCanonicalString"/>.
    /// </summary>
    public required string Revision { get; init; }

    /// <summary>The ordered stages of the bounded journey.</summary>
    public required IReadOnlyList<McpWorkflowViewStageDefinition> Stages { get; init; }

    /// <summary>Maximum number of descriptors the view may publish.</summary>
    public int MaxDescriptors { get; init; } = McpWorkflowViewBudget.MaxDescriptors;

    /// <summary>Maximum aggregate canonical descriptor JSON bytes.</summary>
    public int MaxAggregateDescriptorBytes { get; init; } = McpWorkflowViewBudget.MaxAggregateDescriptorBytes;

    /// <summary>Maximum canonical JSON bytes for any single descriptor.</summary>
    public int MaxDescriptorBytes { get; init; } = McpWorkflowViewBudget.MaxDescriptorBytes;

    /// <summary>
    /// Returns the index of the first stage selecting <paramref name="toolName"/>,
    /// or <c>-1</c> when the view does not cover the tool. First-match-wins keeps
    /// membership single-valued and the ordering deterministic.
    /// </summary>
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

    /// <summary>
    /// Canonical, order-sensitive text form of the definition. The view revision
    /// digest is the SHA-256 of this string, so any change to the stage set,
    /// stage order, or membership rules changes the digest deterministically.
    /// </summary>
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

/// <summary>
/// The initial-release descriptor budget for a workflow view (honua-server#3428).
/// Enforced by a test gate that records measured bytes and estimated model tokens;
/// exceeding a ceiling is a signal to split or refine server-owned stages, never
/// to truncate a schema or silently raise the limit.
/// </summary>
internal static class McpWorkflowViewBudget
{
    /// <summary>Maximum descriptors in one view.</summary>
    public const int MaxDescriptors = 48;

    /// <summary>Maximum aggregate canonical descriptor JSON bytes (128 KiB).</summary>
    public const int MaxAggregateDescriptorBytes = 128 * 1024;

    /// <summary>Maximum canonical JSON bytes for any single descriptor (16 KiB).</summary>
    public const int MaxDescriptorBytes = 16 * 1024;

    /// <summary>
    /// Principal-engineer token estimate used across the MCP measurement tests:
    /// serialized characters / 4.
    /// </summary>
    public static int EstimateTokens(int characters) => characters / 4;
}

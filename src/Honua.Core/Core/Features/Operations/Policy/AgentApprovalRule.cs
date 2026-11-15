// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;

namespace Honua.Core.Features.Operations.Policy;

// An agent principal's state-changing operations need one human approval whether or not
// Operations:Policy is enabled, unless the operation is explicitly approved for agents. The rule
// runs before operator and built-in rules, so neither an operator allow nor the administrator
// exemption can let an agent execute a change directly. Read-only operations follow normal policy.
public static class AgentApprovalRule
{
    public const string AgentRole = "agent";

    public const string ApprovalLane = "control-plane";

    public const string Reason =
        "built-in: an agent's change needs one human approval. " +
        "A proposal was submitted; a person approves it in Console Approvals.";

    // Changes an agent may make without a person. Add an id only with a recorded product decision.
    private static readonly HashSet<string> AgentDirectOperationIds = new(StringComparer.Ordinal);

    // Operations whose MCP tool always files a proposal, for every caller (see PublishedOperationTool).
    private static readonly HashSet<string> GatedOperationIds = new(StringComparer.Ordinal)
    {
        "admin.services.access-policy.set",
        "admin.layer.publish",
        "service.publish",
        "dataset.stage.import",
        StudioDraftMutationContext.PublicationOperationId,
        "studio.content.rollback",
    };

    public static bool IsGated(string operationId) => GatedOperationIds.Contains(operationId);

    public static bool IsAgent(IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        for (var i = 0; i < roles.Count; i++)
        {
            if (string.Equals(roles[i], AgentRole, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool RequiresApproval(IOperationDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return IsGated(descriptor.OperationId)
            || (descriptor.Policy.SideEffectClass != OperationSideEffectClass.ReadOnly
                && !AgentDirectOperationIds.Contains(descriptor.OperationId));
    }

    public static bool Applies(IOperationDescriptor descriptor, IReadOnlyList<string> roles)
        => IsAgent(roles) && RequiresApproval(descriptor);

    public static PolicyDecision Decision => new()
    {
        Kind = PolicyDecisionKind.RequireApproval,
        Reason = Reason,
        ApprovalLane = ApprovalLane,
    };
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Studio.Abstractions;

namespace Honua.Core.Features.Operations.Policy;

// An agent principal's access-policy and publish changes need one human approval whether or not
// Operations:Policy is enabled. The rule runs before operator and built-in rules, so neither an
// operator allow nor the administrator exemption can let an agent execute these directly.
public static class AgentApprovalRule
{
    public const string AgentRole = "agent";

    public const string ApprovalLane = "control-plane";

    public const string Reason =
        "built-in: an agent's access-policy or publish change needs one human approval. " +
        "A proposal was submitted; a person approves it in Console Approvals.";

    private static readonly HashSet<string> GatedOperationIds = new(StringComparer.Ordinal)
    {
        "admin.services.access-policy.set",
        "admin.layer.publish",
        "service.publish",
        StudioDraftMutationContext.PublicationOperationId,
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

    public static bool Applies(string operationId, IReadOnlyList<string> roles)
        => IsGated(operationId) && IsAgent(roles);

    public static PolicyDecision Decision => new()
    {
        Kind = PolicyDecisionKind.RequireApproval,
        Reason = Reason,
        ApprovalLane = ApprovalLane,
    };
}

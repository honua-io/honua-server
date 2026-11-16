// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Features.Authorization;

public sealed class EditionAgentGuardrailPolicy(ILicenseEntitlementService entitlementService)
    : IAgentGuardrailPolicy
{
    private readonly ILicenseEntitlementService _entitlementService =
        entitlementService ?? throw new ArgumentNullException(nameof(entitlementService));

    public AgentGuardrailLevel LevelFor(HonuaEdition edition) => edition switch
    {
        HonuaEdition.Enterprise => AgentGuardrailLevel.Approval,
        HonuaEdition.Pro => AgentGuardrailLevel.Validated,
        _ => AgentGuardrailLevel.Direct,
    };

    public AgentGuardrailDecision Evaluate(AgentOperationDescriptor operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var edition = _entitlementService.GetSnapshot().Edition;
        var level = LevelFor(edition);

        // Read/discovery/query agent operations are never gated: Community keeps
        // full operability and Pro/Enterprise gate only agent-initiated change.
        if (!operation.IsMutating || level == AgentGuardrailLevel.Direct)
        {
            return new AgentGuardrailDecision
            {
                Level = level,
                Edition = edition,
            };
        }

        var reasons = new List<string>(4);

        // Validated and above: plan + dry-run + validate, and a pre-change
        // snapshot/backup gate before apply.
        var requiresValidation = true;
        reasons.Add("agent-change-requires-validation");

        var requiresSnapshot = true;
        var snapshotUnsupported = !operation.SupportsSnapshot;
        reasons.Add(snapshotUnsupported
            ? "pre-change-snapshot-unsupported"
            : "pre-change-snapshot-required");

        // Approval (Enterprise): human-in-the-loop approval plus policy-scoped
        // permissions and immutable audit.
        var requiresApproval = level == AgentGuardrailLevel.Approval;
        var requiresPolicyAudit = level == AgentGuardrailLevel.Approval;
        if (requiresApproval)
        {
            reasons.Add(operation.IsDestructive
                ? "destructive-agent-change-requires-approval"
                : "agent-change-requires-approval");
            reasons.Add("agent-action-policy-audit");
        }

        return new AgentGuardrailDecision
        {
            Level = level,
            Edition = edition,
            RequiresValidation = requiresValidation,
            RequiresSnapshot = requiresSnapshot,
            RequiresApproval = requiresApproval,
            RequiresPolicyAudit = requiresPolicyAudit,
            SnapshotUnsupported = snapshotUnsupported,
            ReasonCodes = reasons,
        };
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

internal enum AgentAuthorityVerdict
{
    Unrestricted,
    ApprovalRequired,
    Denied,
}

// A ceiling only narrows what an agent may do; it never grants a scope or removes an approval.
internal static class AgentAuthorityPolicy
{
    public static async Task<AgentAuthorityVerdict> EvaluateAsync(IAgentAuthorityStore? store, IOperationDescriptor descriptor,
        OperationPolicyContext context, CancellationToken cancellationToken)
    {
        if (!AgentApprovalRule.IsAgent(context.Roles))
            return AgentAuthorityVerdict.Unrestricted;
        if (string.IsNullOrWhiteSpace(context.PrincipalId))
            return AgentAuthorityVerdict.Denied;
        // Without a stored ceiling (never set, or deleted) an agent's change becomes a proposal for one
        // human approval; read-only operations follow normal policy. A disabled ceiling revokes
        // everything. A store that cannot be read throws, so the operation fails closed.
        var ceiling = store is null
            ? null
            : await store.GetAsync(context.TenantId, context.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (ceiling is null)
        {
            return descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly
                ? AgentAuthorityVerdict.Unrestricted
                : AgentAuthorityVerdict.ApprovalRequired;
        }

        return Within(ceiling, descriptor, context) ? AgentAuthorityVerdict.Unrestricted : AgentAuthorityVerdict.Denied;
    }

    private static bool Within(AgentAuthorityCeiling ceiling, IOperationDescriptor descriptor, OperationPolicyContext context)
        => ceiling.Enabled && ceiling.IsValid()
            && string.Equals(ceiling.TenantId, context.TenantId, StringComparison.Ordinal)
            && string.Equals(ceiling.PrincipalId, context.PrincipalId, StringComparison.Ordinal)
            && Enum.IsDefined(descriptor.Policy.BlastRadiusClass) && Enum.IsDefined(descriptor.Policy.SideEffectClass)
            && ceiling.AllowedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal)
            && descriptor.Policy.BlastRadiusClass <= ceiling.MaximumBlastRadius
            && descriptor.Policy.SideEffectClass <= ceiling.MaximumSideEffect;
}

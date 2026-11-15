// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;

namespace Honua.Core.Features.Operations.Policy;

internal static class AgentAuthorityPolicy
{
    public static async Task<bool> AllowsAsync(IAgentAuthorityStore? store, IOperationDescriptor descriptor,
        OperationPolicyContext context, CancellationToken cancellationToken)
    {
        if (!AgentApprovalRule.IsAgent(context.Roles))
            return true;
        // Without a stored ceiling (never set, or deleted) an agent keeps read-only operations,
        // which its scopes still gate, and nothing that changes state. A disabled ceiling revokes
        // everything. A store that cannot be read throws, so the operation fails closed.
        if (string.IsNullOrWhiteSpace(context.PrincipalId))
            return false;
        var ceiling = store is null
            ? null
            : await store.GetAsync(context.TenantId, context.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (ceiling is null)
            return descriptor.Policy.SideEffectClass == OperationSideEffectClass.ReadOnly;
        return ceiling.Enabled && ceiling.IsValid()
            && string.Equals(ceiling.TenantId, context.TenantId, StringComparison.Ordinal)
            && string.Equals(ceiling.PrincipalId, context.PrincipalId, StringComparison.Ordinal)
            && Enum.IsDefined(descriptor.Policy.BlastRadiusClass) && Enum.IsDefined(descriptor.Policy.SideEffectClass)
            && ceiling.AllowedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal)
            && descriptor.Policy.BlastRadiusClass <= ceiling.MaximumBlastRadius
            && descriptor.Policy.SideEffectClass <= ceiling.MaximumSideEffect;
    }
}

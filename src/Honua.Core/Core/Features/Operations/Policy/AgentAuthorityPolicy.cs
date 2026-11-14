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
        if (store is null || string.IsNullOrWhiteSpace(context.PrincipalId))
            return false;
        var ceiling = await store.GetAsync(context.TenantId, context.PrincipalId, cancellationToken).ConfigureAwait(false);
        return ceiling is { Enabled: true } && ceiling.IsValid()
            && string.Equals(ceiling.TenantId, context.TenantId, StringComparison.Ordinal)
            && string.Equals(ceiling.PrincipalId, context.PrincipalId, StringComparison.Ordinal)
            && Enum.IsDefined(descriptor.Policy.BlastRadiusClass) && Enum.IsDefined(descriptor.Policy.SideEffectClass)
            && ceiling.AllowedOperationIds.Contains(descriptor.OperationId, StringComparer.Ordinal)
            && descriptor.Policy.BlastRadiusClass <= ceiling.MaximumBlastRadius
            && descriptor.Policy.SideEffectClass <= ceiling.MaximumSideEffect;
    }
}

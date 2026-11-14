// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;

namespace Honua.Core.Features.Operations.Services;

internal static class OperationAuditActor
{
    internal static AuditActorType ResolveType(OperationPolicyContext context)
        => context.PrincipalId is null
            ? AuditActorType.Anonymous
            : AgentApprovalRule.IsAgent(context.Roles) ? AuditActorType.Agent : AuditActorType.UserId;
}

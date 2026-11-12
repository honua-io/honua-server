// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Admin;

// An agent's service access-policy change needs one human approval. An approved proposal replays
// through this route with a server-minted credential, which is not an agent, so it passes.
internal static class AgentAccessPolicyApprovalFilter
{
    internal const string ApprovalPolicyRef = "operator.agent.access-policy";

    public static ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!AgentPrincipal.IsAgent(context.User))
        {
            return next(invocation);
        }

        var refusal = context.RequestServices.GetRequiredService<OperatorApprovalGate>().EvaluateApproval(
            context,
            OperatorResourceType.PublishedService,
            OperatorOperation.Update,
            requiresExplicitApproval: true,
            approvalPolicyRef: ApprovalPolicyRef);
        return refusal is null ? next(invocation) : ValueTask.FromResult<object?>(refusal);
    }
}

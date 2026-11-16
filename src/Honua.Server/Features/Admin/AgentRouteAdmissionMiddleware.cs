// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

// Refuses an agent that calls a governed route (admin, Console, or any route that requires
// administrator authority) to change something, unless the route applies its own agent rule, or
// the request was dispatched by the operations gateway (it carries a verified operation lineage,
// so the operation policy and its approval rule have already decided). New routes are covered
// without being listed.
internal sealed class AgentRouteAdmissionMiddleware(RequestDelegate next)
{
    internal const string PolicyRef = "operator.agent.admin-route";

    internal const string Guidance =
        "Agents change administrative settings through the operations API (POST /api/v1/operations/{operationId}/submit) " +
        "or the matching MCP tool, which runs the change or files a proposal for one human approval, as the operation policy decides. " +
        "If no operation covers this change, ask a person to make it. Do not retry with another credential.";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return IsRefused(context)
            ? OperatorApprovalGate.AgentApprovalRequired(context, PolicyRef, Guidance).ExecuteAsync(context)
            : next(context);
    }

    internal static bool IsRefused(HttpContext context)
        => AgentChangeRoutes.IsChange(context.Request.Method)
            && AgentPrincipal.IsAgent(context.User)
            && context.GetEndpoint() is RouteEndpoint endpoint
            && AgentChangeRoutes.IsGoverned(endpoint)
            && !AgentChangeRoutes.HasRouteAgentRule(endpoint)
            && context.Features.Get<AttestedOperationLineage>() is null;
}

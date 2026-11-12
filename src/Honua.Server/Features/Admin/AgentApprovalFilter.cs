// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

// Changes to who can read, write or reach published content need a person when an agent asks.
// Access-policy changes and layer publishes can be filed as proposals and replay here with a
// server-minted credential, which is not an agent; the other changes are made in Console. The
// refusal does not depend on OperatorApproval settings.
internal static class AgentApprovalFilter
{
    internal const string AccessPolicyRef = "operator.agent.access-policy";
    internal const string PublishRef = "operator.agent.publish";
    internal const string AccessGrantsRef = "operator.agent.access-grants";
    internal const string OperationRef = "operator.agent.operation";

    private const string ProposalGuidance = OperatorApprovalGate.AgentApprovalGuidance;

    internal const string ConsoleGuidance =
        "Agent changes to layers, access grants and published content need a person: ask one to make this change in Console. " +
        "Do not retry with another credential.";

    public static ValueTask<object?> AccessPolicy(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, AccessPolicyRef, ProposalGuidance);

    public static ValueTask<object?> Publish(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, PublishRef, ProposalGuidance);

    public static ValueTask<object?> LayerState(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, PublishRef, ConsoleGuidance);

    public static ValueTask<object?> AccessGrants(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, AccessGrantsRef, ConsoleGuidance);

    public static ValueTask<object?> LayerMetadataAccessPolicy(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => invocation.Arguments.OfType<UpdateLayerMetadataRequest>().Any(static request => request.AccessPolicy is not null)
            ? Refuse(invocation, next, AccessPolicyRef, ConsoleGuidance)
            : next(invocation);

    private static ValueTask<object?> Refuse(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next,
        string policyRef,
        string guidance)
    {
        var context = invocation.HttpContext;
        return AgentPrincipal.IsAgent(context.User)
            ? ValueTask.FromResult<object?>(OperatorApprovalGate.AgentApprovalRequired(context, policyRef, guidance))
            : next(invocation);
    }
}

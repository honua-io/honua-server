// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Console.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;

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
    internal const string LayerStateRef = "operator.agent.layer-state";
    internal const string ContentVisibilityRef = "operator.agent.content-visibility";

    private const string ProposalGuidance = OperatorApprovalGate.AgentApprovalGuidance;

    internal const string ConsoleGuidance =
        "Agent changes to layers, access grants and published content need a person: ask one to make this change in Console. " +
        "Do not retry with another credential.";

    public static ValueTask<object?> AccessPolicy(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, AccessPolicyRef, ProposalGuidance);

    public static ValueTask<object?> Publish(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, PublishRef, ProposalGuidance);

    public static ValueTask<object?> LayerState(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, LayerStateRef, ConsoleGuidance);

    // Any visibility beyond personal, or a team scope, puts content in front of a new audience.
    public static async ValueTask<object?> ContentVisibility(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!AgentPrincipal.IsAgent(context.User))
            return await next(invocation).ConfigureAwait(false);

        var changesAudience = invocation.Arguments.Any(static argument => WidensVisibility(argument));
        if (invocation.Arguments.OfType<CreateConsoleContentItemRequest>().FirstOrDefault() is { OwnerId: not null } create)
        {
            changesAudience |= !string.Equals(create.OwnerId, ConsolePrincipal.ResolveActorId(context.User), StringComparison.Ordinal);
        }

        if (!changesAudience && invocation.Arguments.OfType<UpdateConsoleContentItemRequest>().FirstOrDefault() is { } update)
        {
            var store = context.RequestServices.GetRequiredService<ProjectedConsoleContentStore>();
            var id = context.Request.RouteValues["id"]?.ToString();
            if (id is not null && await store.GetAsync(id, context.RequestAborted).ConfigureAwait(false) is { } existing)
            {
                if (update.Generation is { } generation && generation != existing.Generation)
                {
                    return TypedResults.Conflict(ApiResponse<object>.Failure("Console content item generation has changed."));
                }

                changesAudience = !string.Equals(update.OwnerId, existing.OwnerId, StringComparison.Ordinal);
                // Persist against the same version whose ownership was authorized.
                update.AuthorizedGeneration = existing.Generation;
            }
        }

        return changesAudience
            ? OperatorApprovalGate.AgentApprovalRequired(context, ContentVisibilityRef, ConsoleGuidance)
            : await next(invocation).ConfigureAwait(false);
    }

    private static bool WidensVisibility(object? argument) => argument switch
    {
        CreateConsoleContentItemRequest create => IsWide(create.Visibility) || create.TeamScopeId is not null,
        UpdateConsoleContentItemRequest update => IsWide(update.Visibility) || update.TeamScopeId is not null,
        PatchConsoleContentItemRequest patch => IsWide(patch.Visibility),
        _ => false,
    };

    private static bool IsWide(ConsoleVisibility? visibility)
        => visibility is not null and not ConsoleVisibility.Personal;

    public static ValueTask<object?> AccessGrants(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, AccessGrantsRef, ConsoleGuidance);

    public static ValueTask<object?> LayerMetadataAccessPolicy(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => invocation.Arguments.OfType<UpdateLayerMetadataRequest>().Any(static request => request.AccessPolicy is not null || request.Editing is not null)
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

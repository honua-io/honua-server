// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Console;
using Honua.Server.Features.Console.Models;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

// Changes to who can read, write or reach published content need a person when an agent asks.
// Access-policy changes, layer metadata changes and layer publishes can be filed as proposals and
// replay here with a server-minted credential, which is not an agent; the other changes are made
// in Console. The refusal does not depend on OperatorApproval settings.
internal static class AgentApprovalFilter
{
    internal const string AccessPolicyRef = "operator.agent.access-policy";
    internal const string PublishRef = "operator.agent.publish";
    internal const string AccessGrantsRef = "operator.agent.access-grants";
    internal const string OperationRef = "operator.agent.operation";
    internal const string LayerStateRef = "operator.agent.layer-state";
    internal const string ContentVisibilityRef = "operator.agent.content-visibility";
    internal const string LayerMetadataRef = "operator.agent.layer-metadata";

    private const string ProposalGuidance = OperatorApprovalGate.AgentApprovalGuidance;

    private static readonly HashSet<string> MetadataOperationIds = new(StringComparer.Ordinal)
    {
        "admin.metadata.layer-discovery.set",
        "admin.metadata.service-discovery.set",
    };

    internal const string ConsoleGuidance =
        "Agent changes to layers, access grants and published content need a person: ask one to make this change in Console. " +
        "Do not retry with another credential.";

    // Attaches one of these rules to an admin or Console route. The route then decides for itself
    // what an agent may change, so the default refusal (AgentRouteAdmissionMiddleware) leaves it
    // to the rule and its specific guidance.
    public static TBuilder AddAgentApprovalFilter<TBuilder>(
        this TBuilder builder,
        Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> rule,
        [CallerArgumentExpression(nameof(rule))] string ruleName = "")
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(rule).WithRouteAgentRule($"Its own agent rule decides ({ruleName}).");

    public static ValueTask<object?> AccessPolicy(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, AccessPolicyRef, ProposalGuidance);

    public static ValueTask<object?> Publish(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, PublishRef, ProposalGuidance);

    public static ValueTask<object?> LayerState(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(invocation, next, LayerStateRef, ConsoleGuidance);

    // An agent may create Console content it owns and keeps personal, and edit that content.
    // A wider visibility, a team scope or another owner puts content in front of a new audience,
    // and editing content someone else owns, or content others can see or reach through sharing,
    // changes their work: both need a person.
    public static async ValueTask<object?> ContentVisibility(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!AgentPrincipal.IsAgent(context))
            return await next(invocation).ConfigureAwait(false);

        var actorId = ConsolePrincipal.ResolveActorId(context.User);
        if (invocation.Arguments.Any(argument => WidensVisibility(argument) || CreatesForAnotherOwner(argument, actorId)))
            return OperatorApprovalGate.AgentApprovalRequired(context, ContentVisibilityRef, ConsoleGuidance);

        var edit = invocation.Arguments.FirstOrDefault(static argument => argument is UpdateConsoleContentItemRequest or PatchConsoleContentItemRequest);
        var id = context.Request.RouteValues["id"]?.ToString();
        if (edit is null || id is null)
            return await next(invocation).ConfigureAwait(false);

        var store = context.RequestServices.GetRequiredService<ProjectedConsoleContentStore>();
        var existing = await store.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        // A missing item is the handler's 404.
        var refusal = existing is null ? null : await AuthorizeEditAsync(context, edit, existing).ConfigureAwait(false);
        return refusal ?? await next(invocation).ConfigureAwait(false);
    }

    private static async Task<IResult?> AuthorizeEditAsync(HttpContext context, object edit, ConsoleContentItem existing)
    {
        var owner = ConsolePrincipal.ResolveStableActorId(context.User);
        if (owner is null || !existing.IsPersonalTo(owner) || ChangesOwner(edit, existing)
            || await IsSharedAsync(context, existing.Id).ConfigureAwait(false))
        {
            return OperatorApprovalGate.AgentApprovalRequired(context, ContentVisibilityRef, ConsoleGuidance);
        }

        switch (edit)
        {
            case UpdateConsoleContentItemRequest { Generation: { } generation } when generation != existing.Generation:
                return TypedResults.Conflict(ApiResponse<object>.Failure("Console content item generation has changed."));
            // A replacement is bound to the version whose owner was authorized; it always keeps the item personal.
            case UpdateConsoleContentItemRequest update:
                update.AuthorizedGeneration = existing.Generation;
                break;
            // A patch keeps the stored audience, so the write itself re-checks that the item is still personal to the agent.
            case PatchConsoleContentItemRequest patch:
                patch.AuthorizedPersonalOwnerId = owner;
                break;
        }
        return null;
    }

    private static async Task<bool> IsSharedAsync(HttpContext context, string id)
    {
        var shares = context.RequestServices.GetRequiredService<IConsoleShareStore>();
        var share = await shares.GetAsync(id, context.RequestAborted).ConfigureAwait(false);
        return share is not null
            && (share.AccessTier != ConsoleShareAccessTier.Private
                || share.EmbedEnabled
                || share.PublicLinkTokens.Any(static link => !link.IsExpired));
    }

    private static bool ChangesOwner(object edit, ConsoleContentItem existing)
        => edit is UpdateConsoleContentItemRequest update
            && !string.Equals(update.OwnerId, existing.OwnerId, StringComparison.Ordinal);

    private static bool CreatesForAnotherOwner(object? argument, string? actorId)
        => argument is CreateConsoleContentItemRequest { OwnerId: { } owner }
            && !string.Equals(owner, actorId, StringComparison.Ordinal);

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

    // Every field on this route changes a published layer, so an agent files it as a proposal
    // (admin.services.layer-metadata.set); access and editing changes keep their own policy ref.
    public static ValueTask<object?> LayerMetadata(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
        => Refuse(
            invocation,
            next,
            invocation.Arguments.OfType<UpdateLayerMetadataRequest>().Any(static request => request.AccessPolicy is not null || request.Editing is not null)
                ? AccessPolicyRef
                : LayerMetadataRef,
            ProposalGuidance);

    // People with the right role edit metadata directly; changes from an AI agent or a stored API
    // key become a proposal approved by one person. Approved proposals replay here with a
    // server-minted credential; a gateway dispatch carries attested lineage because the gateway
    // already applied MetadataApprovalRefusal to its caller.
    public static ValueTask<object?> MetadataDiscovery(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (context.Features.Get<AttestedOperationLineage>() is not null)
        {
            return next(invocation);
        }

        return MetadataApprovalRefusal(context, operationId: null) is { } refusal
            ? ValueTask.FromResult<object?>(refusal)
            : next(invocation);
    }

    // The operations gateway admits a standard stored key through the administrator exemption, so
    // the metadata operations refuse it there too; operationId null means a direct metadata route.
    internal static IResult? MetadataApprovalRefusal(HttpContext context, string? operationId)
    {
        if (operationId is not null && !MetadataOperationIds.Contains(operationId))
        {
            return null;
        }

        var user = context.User;
        var isStoredApiKey = string.Equals(user.FindFirst("auth_type")?.Value, "admin-api-key", StringComparison.OrdinalIgnoreCase);
        return !AdminApiKeyPermission.IsServerMintedApprovedOperation(user) && (AgentPrincipal.IsAgent(context) || isStoredApiKey)
            ? OperatorApprovalGate.AgentApprovalRequired(context, LayerMetadataRef, ProposalGuidance)
            : null;
    }

    private static ValueTask<object?> Refuse(
        EndpointFilterInvocationContext invocation,
        EndpointFilterDelegate next,
        string policyRef,
        string guidance)
    {
        var context = invocation.HttpContext;
        return AgentPrincipal.IsAgent(context)
            ? ValueTask.FromResult<object?>(OperatorApprovalGate.AgentApprovalRequired(context, policyRef, guidance))
            : next(invocation);
    }
}

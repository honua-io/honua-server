// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Styling;

/// <summary>Server-only capability derived from the canonical stored executing proposal and its sealed plan.</summary>
internal sealed class SharedStyleApprovedReplay
{
    private SharedStyleApprovedReplay(SharedStyleApprovalPayload payload, ResolvedStyleEdit expected,
        IServiceProvider services, OperationRequest request, OperationPolicyContext policy, HttpContext context,
        StyleEditImpactService impacts)
    { Payload = payload; Expected = expected; _services = services; _request = request; _policy = policy; _context = context; _impacts = impacts; }

    private readonly IServiceProvider _services;
    private readonly OperationRequest _request;
    private readonly OperationPolicyContext _policy;
    private readonly HttpContext _context;
    private readonly StyleEditImpactService _impacts;
    internal SharedStyleApprovalPayload Payload { get; }
    internal ResolvedStyleEdit Expected { get; }
    internal StyleMutationReceipt? Receipt { get; private init; }
    internal StyleMutationIdentity Identity => new(
        _policy.OperationInstanceId ?? throw new InvalidOperationException("Missing canonical operation instance."),
        _policy.ApprovedProposalId ?? throw new InvalidOperationException("Missing canonical proposal."),
        _policy.ApprovedPlanHash ?? throw new InvalidOperationException("Missing sealed canonical plan."),
        Payload.ActorId, Payload.TenantId, Payload.SchemaName ?? "honua", Payload.TargetId,
        Payload.StoreSnapshot.StyleId, Payload.Impact.StyleVersion, Payload.Impact.ContentHash,
        Payload.Impact.ImpactHash ?? throw new InvalidOperationException("Missing reviewed impact hash."),
        StyleEditHashes.Content(Payload.Candidate.MapLibreJson),
        StyleEditHashes.Content(string.IsNullOrWhiteSpace(Payload.StoreSnapshot.LayerStyle is { } layer
            ? layer.MapLibreStyleJson : Payload.StoreSnapshot.CatalogStyle?.MapLibreStyleJson)
            ? "null" : (Payload.StoreSnapshot.LayerStyle is { } storedLayer
                ? storedLayer.MapLibreStyleJson : Payload.StoreSnapshot.CatalogStyle!.MapLibreStyleJson)!));

    internal static async Task<SharedStyleApprovedReplay> VerifyAsync(IServiceProvider services, OperationRequest request,
        OperationPolicyContext context, CancellationToken cancellationToken, bool receiptOnly = false)
    {
        var payload = await RequireProposalAsync(services, request, context, cancellationToken, receiptOnly).ConfigureAwait(false);
        if (payload.TenantId != context.TenantId || payload.TenantId != services.GetRequiredService<ITenantContext>().TenantId ||
            payload.SchemaName != context.SchemaName || payload.SchemaName != services.GetService<ISchemaContext>()?.CurrentSchema) Deny();
        var key = await services.GetRequiredService<IAdminApiKeyStore>().GetAsync(payload.KeyId, cancellationToken).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (key is null || key.RevokedAt is not null || key.ExpiresAt <= now ||
            !key.Permissions.Order(StringComparer.Ordinal).SequenceEqual(payload.Permissions.Order(StringComparer.Ordinal))) Deny();
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(key!.Id, key.Name, key.Permissions);
        if (CanonicalSecurityActor.Resolve(principal)?.ActorId != payload.ActorId || !principal.HasClaim("permission", "agent:workspace") ||
            payload.AuthorSecurityContext.OwnerActorId != payload.ActorId || payload.AuthorSecurityContext.TenantId != payload.TenantId ||
            !payload.AuthorSecurityContext.Claims.Where(claim => claim.Type == "permission").Select(claim => claim.Value)
                .Order(StringComparer.Ordinal).SequenceEqual(payload.Permissions.Order(StringComparer.Ordinal))) Deny();
        await RequireRolesAsync(services, payload.AuthorSecurityContext, cancellationToken).ConfigureAwait(false);
        var original = new DefaultHttpContext { User = principal, RequestServices = services };
        original.Request.Method = HttpMethods.Post;
        original.Request.Headers["X-Honua-Workspace"] = payload.WorkspaceId;
        original.Request.Headers["X-Honua-Turn"] = payload.TurnId;
        var impacts = new StyleEditImpactService(services.GetRequiredService<IStyleCatalog>(),
            services.GetRequiredService<IMetadataV2GraphProvider>(), new SharedStyleWorkspaceAuthorization.PrivateAccessor { HttpContext = original });
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(original, principal, payload.WorkspaceId, payload.TurnId, cancellationToken).ConfigureAwait(false);
        RequireBinding(payload, turn);
        await RequireRestoreAsync(original, turn, payload, cancellationToken).ConfigureAwait(false);
        var current = await impacts.ResolveAsync(payload.Impact.StyleId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.PrimaryResource?.Metadata.Id != payload.PrimaryResourceId) Deny();
        var impact = payload.Impact with { CanonicalLayerId = payload.StoreSnapshot.LayerStyle?.LayerId };
        var expected = new ResolvedStyleEdit(impact, payload.StoreSnapshot, current!.PrimaryResource, current.CanonicalStyleJson);
        var replay = new SharedStyleApprovedReplay(payload, expected, services, request, context, original, impacts);
        var receipt = await services.GetRequiredService<IStyleCatalog>().GetMutationReceiptAsync(replay.Identity, cancellationToken).ConfigureAwait(false);
        if (receipt is not null)
        {
            if (!current.CompleteReadableScope || !current.Impact.ImpactComplete) Deny();
            return new(payload, expected, services, request, context, original, impacts) { Receipt = receipt };
        }
        if (receiptOnly) return replay; // No receipt: read-only recovery cannot prepare or execute a mutation.
        SharedStyleAgentEditor.RequireTask(turn, current!);
        SharedStyleEditor.RequireUnchanged(expected, current);
        // Updated validation policy cannot quietly reinterpret the approved bytes.
        var (candidate, error) = await SharedStyleCandidatePreparation.PrepareAsync(current, payload.Candidate.MapLibreJson,
            false, original, cancellationToken).ConfigureAwait(false);
        if (error is not null || candidate is null || StyleEditHashes.Content(candidate.MapLibreJson) != StyleEditHashes.Content(payload.Candidate.MapLibreJson) ||
            StyleEditHashes.Content(candidate.DrawingInfoJson) != StyleEditHashes.Content(payload.Candidate.DrawingInfoJson)) Deny();
        return new(payload, expected, services, request, context, original, impacts);
    }

    internal async Task RevalidateAsync(CancellationToken cancellationToken)
    {
        // Repeat all mutable authority after waiting for the canonical store lock.
        _ = await RequireProposalAsync(_services, _request, _policy, cancellationToken).ConfigureAwait(false);
        await RequireRolesAsync(_services, Payload.AuthorSecurityContext, cancellationToken).ConfigureAwait(false);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(_context, _context.User,
            Payload.WorkspaceId, Payload.TurnId, cancellationToken).ConfigureAwait(false);
        RequireBinding(Payload, turn);
        await RequireRestoreAsync(_context, turn, Payload, cancellationToken).ConfigureAwait(false);
        var current = await _impacts.ResolveAsync(Payload.Impact.StyleId, cancellationToken).ConfigureAwait(false);
        SharedStyleEditor.RequireUnchanged(Expected, current);
        if (current is not null) SharedStyleAgentEditor.RequireTask(turn, current);
    }

    internal async Task RevalidateReceiptAsync(CancellationToken cancellationToken)
    {
        _ = await RequireProposalAsync(_services, _request, _policy, cancellationToken, true).ConfigureAwait(false);
        await RequireRolesAsync(_services, Payload.AuthorSecurityContext, cancellationToken).ConfigureAwait(false);
        var turn = await SharedStyleWorkspaceAuthorization.AuthorizeAsync(_context, _context.User,
            Payload.WorkspaceId, Payload.TurnId, cancellationToken).ConfigureAwait(false);
        RequireBinding(Payload, turn);
        var current = await _impacts.ResolveAsync(Payload.Impact.StyleId, cancellationToken).ConfigureAwait(false);
        if (current is null || !current.CompleteReadableScope || !current.Impact.ImpactComplete ||
            current.PrimaryResource?.Metadata.Id != Payload.PrimaryResourceId) Deny();
    }

    private static async Task RequireRestoreAsync(HttpContext context, SharedStyleTurnBinding turn,
        SharedStyleApprovalPayload payload, CancellationToken cancellationToken)
    {
        if (payload.RestoreSourceProposalId is not { } source) return;
        var restored = await SharedStyleHistory.LoadRestoreAsync(context, turn, source, cancellationToken).ConfigureAwait(false);
        if (StyleEditHashes.Content(restored.Style.GetRawText()) != StyleEditHashes.Content(payload.Candidate.MapLibreJson)) Deny();
    }

    private static void RequireBinding(SharedStyleApprovalPayload payload, SharedStyleTurnBinding turn)
    {
        if (turn.Binding.OwnerActorId != payload.OwnerActorId || turn.Binding.TargetId != payload.TargetId ||
            turn.Binding.ReleaseId != payload.ReleaseId || turn.Binding.TenantId != payload.TenantId) Deny();
    }

    private static async Task RequireRolesAsync(IServiceProvider services, JobSecurityContext snapshot, CancellationToken cancellationToken)
    {
        var membership = await JobSecurityContextCapture.RevalidateRoleMembershipAsync(snapshot,
            services.GetService<IPrincipalMembershipSource>(), services.GetService<IOptions<RbacOptions>>()?.Value ?? new(), cancellationToken).ConfigureAwait(false);
        if (membership.HasRemovedRoles || membership.Status is JobSecurityContextMembershipStatus.Changed or
            JobSecurityContextMembershipStatus.Inactive or JobSecurityContextMembershipStatus.ManagedUnresolved) Deny();
    }

    private static async Task<SharedStyleApprovalPayload> RequireProposalAsync(IServiceProvider services, OperationRequest request,
        OperationPolicyContext context, CancellationToken cancellationToken, bool receiptOnly = false)
    {
        if (request.OperationId != SharedStyleEditOperation.OperationId || string.IsNullOrWhiteSpace(context.ApprovedProposalId) ||
            string.IsNullOrWhiteSpace(context.ApprovedPlanHash)) Deny();
        var proposal = await services.GetRequiredService<IOperationProposalStore>().GetAsync(context.ApprovedProposalId!, cancellationToken).ConfigureAwait(false);
        if (proposal is null || (proposal.Status != OperationProposalStatus.Executing && !(receiptOnly && proposal.Status == OperationProposalStatus.Succeeded)) || proposal.OperationId != request.OperationId ||
            proposal.RequestedBy != context.PrincipalId || proposal.Audit.OperationInstanceId != context.OperationInstanceId ||
            proposal.SealedPlanHash != context.ApprovedPlanHash || proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan) ||
            proposal.Plan.ExecutionPayload is null || !request.Parameters.TryGetValue("sealedPayload", out var supplied) ||
            supplied != proposal.Plan.ExecutionPayload) Deny();
        if (receiptOnly && (string.IsNullOrWhiteSpace(proposal!.ApprovedByAtClaim) || proposal.ApprovedAtClaim is null ||
            proposal.ApprovedByAtClaim.Equals(proposal.RequestedBy, StringComparison.OrdinalIgnoreCase))) Deny();
        var payload = JsonSerializer.Deserialize(proposal!.Plan.ExecutionPayload!, SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload)
            ?? throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
        if (payload.ActorId != proposal.RequestedBy) Deny();
        return payload;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}

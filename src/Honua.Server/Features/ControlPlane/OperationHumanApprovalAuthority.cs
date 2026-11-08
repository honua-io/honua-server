// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.ControlPlane;

internal sealed partial class OperationHumanApprovalAuthority(
    IServiceScopeFactory scopes,
    IDataProtectionProvider protection,
    IConfiguration configuration,
    TimeProvider timeProvider,
    IHttpContextAccessor requestContext) : IOperationHumanApprovalVerifier
{
    internal const string CapabilityHeader = "X-Honua-Approval-Capability";
    private const int MaximumCapabilityLength = 16384;
    private static readonly TimeSpan CapabilityLifetime = TimeSpan.FromMinutes(5);
    private readonly IDataProtector _protector = protection.CreateProtector("Honua.OperationHumanApproval.v1");

    public async Task<OperationHumanApprovalReview> OpenReviewAsync(
        OperationProposal proposal, HttpContext context)
    {
        ValidateBoundProposal(proposal);
        var grant = await OpenCapabilityAsync(ProposalTarget(proposal), context).ConfigureAwait(false);
        return new OperationHumanApprovalReview
        {
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            PlanHash = proposal.SealedPlanHash!,
            ExpiresAt = grant.ExpiresAt,
            Capability = grant.Capability,
        };
    }

    public async Task<OperationHumanApprovalEvidence> VerifyAsync(
        OperationProposal proposal, OperationProposalApprovalContext approval, CancellationToken cancellationToken = default)
    {
        ValidateBoundProposal(proposal);
        var verified = await VerifyCapabilityAsync(
            ProposalTarget(proposal), approval, cancellationToken).ConfigureAwait(false);
        return new OperationHumanApprovalEvidence
        {
            Version = OperationHumanApprovalEvidence.CurrentVersion,
            ActorId = verified.ActorId,
            Channel = OperationHumanApprovalEvidence.InteractiveOidcChannel,
            SessionReference = verified.SessionReference,
            CapabilityReference = verified.CapabilityReference,
            PlanHash = proposal.SealedPlanHash!,
            InitiationDigest = ComputeInitiationDigest(proposal.Initiation!),
            BindingDigest = ComputeBindingDigest(proposal),
            ProposalVersion = proposal.Version,
            ApprovedAt = verified.ApprovedAt,
        };
    }

    private async Task<HumanApprovalGrant> OpenCapabilityAsync(HumanApprovalTarget target, HttpContext context)
    {
        if (!ReferenceEquals(context, requestContext.HttpContext)
            || context.GetEndpoint()?.Metadata.GetMetadata<AdminHumanApprovalEndpointMetadata>() != AdminHumanApprovalEndpointMetadata.Review
            || !HttpMethods.IsGet(context.Request.Method))
            throw new HumanApprovalRequiredException();
        RefuseNonHumanTransport(context);
        var tenant = ResolveRequestTenant(target, context);
        if (!string.Equals(target.TenantId, tenant, StringComparison.Ordinal))
            throw new HumanApprovalRequiredException();
        var human = await LoadHumanAsync(context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName],
            target, context.RequestAborted).ConfigureAwait(false);
        if (CanonicalSecurityActor.Resolve(context.User)?.ActorId != human.ActorId)
            throw new HumanApprovalRequiredException();
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + CapabilityLifetime;
        if (human.ExpiresAt < expiresAt) expiresAt = human.ExpiresAt;
        if (target.ExpiresAt is { } targetExpiry && targetExpiry < expiresAt) expiresAt = targetExpiry;
        if (expiresAt <= now) throw new HumanApprovalRequiredException();
        var capability = new OperationHumanApprovalCapability
        {
            Version = OperationHumanApprovalEvidence.CurrentVersion,
            Decision = "approve",
            Audience = ResolveAudience(),
            TargetKind = target.Kind,
            TargetId = target.Id,
            TargetVersion = target.Version,
            BindingDigest = target.BindingDigest,
            ActorId = human.ActorId,
            TenantId = target.TenantId,
            SessionReference = human.SessionReference,
            ClaimsDigest = human.ClaimsDigest,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            IssuedAt = now,
            ExpiresAt = expiresAt,
        };
        return new HumanApprovalGrant(expiresAt, _protector.Protect(JsonSerializer.Serialize(
            capability, OperationHumanApprovalJsonContext.Default.OperationHumanApprovalCapability)));
    }

    private async Task<VerifiedHumanDecision> VerifyCapabilityAsync(
        HumanApprovalTarget target, OperationProposalApprovalContext approval, CancellationToken cancellationToken)
    {
        var context = requestContext.HttpContext ?? throw new HumanApprovalRequiredException();
        if (context.GetEndpoint()?.Metadata.GetMetadata<AdminHumanApprovalEndpointMetadata>() != AdminHumanApprovalEndpointMetadata.Approve
            || !HttpMethods.IsPost(context.Request.Method))
            throw new HumanApprovalRequiredException();
        RefuseNonHumanTransport(context);
        var tokens = context.Request.Headers[CapabilityHeader];
        if (tokens.Count != 1) throw new HumanApprovalRequiredException();
        var capability = ReadCapability(tokens[0]);
        var now = timeProvider.GetUtcNow();
        if (capability.Version != OperationHumanApprovalEvidence.CurrentVersion
            || capability.Decision != "approve" || capability.ExpiresAt <= now || capability.IssuedAt > now
            || capability.ExpiresAt - capability.IssuedAt > CapabilityLifetime
            || capability.Audience != ResolveAudience() || capability.TargetKind != target.Kind
            || capability.TargetId != target.Id || capability.TargetVersion != target.Version
            || capability.BindingDigest != target.BindingDigest || capability.TenantId != target.TenantId
            || capability.TenantId != (string.IsNullOrEmpty(approval.TenantId) ? null : approval.TenantId)
            || capability.TenantId != ResolveRequestTenant(target, context))
            throw new HumanApprovalRequiredException();
        var human = await LoadHumanAsync(context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName],
            target, cancellationToken).ConfigureAwait(false);
        if (capability.ActorId != human.ActorId || capability.ActorId != approval.ApprovedBy
            || CanonicalSecurityActor.Resolve(context.User)?.ActorId != human.ActorId
            || capability.SessionReference != human.SessionReference || capability.ClaimsDigest != human.ClaimsDigest
            || capability.ExpiresAt > human.ExpiresAt
            || (target.ExpiresAt is { } targetExpiry && capability.ExpiresAt > targetExpiry))
            throw new HumanApprovalRequiredException();
        return new VerifiedHumanDecision(human.ActorId, human.SessionReference,
            Hash(Encoding.UTF8.GetBytes(capability.Nonce)), now);
    }

    private static string? ResolveRequestTenant(HumanApprovalTarget target, HttpContext context)
    {
        var requestTenant = context.RequestServices.GetService<ITenantContext>();
        // A platform-owned workflow stays global when middleware supplied only the anonymous default.
        return target.TenantId is null && requestTenant?.Source == TenantContextSource.Default
            ? null : requestTenant?.TenantId;
    }

    private static HumanApprovalTarget ProposalTarget(OperationProposal proposal) => new(
        "operation-proposal", proposal.ProposalId, proposal.Version, proposal.TenantId,
        ComputeBindingDigest(proposal), proposal.Evidence?.ExpiresAt,
        principal => OperationApprovalAuthorization.PermitsOperationScope(principal, proposal));

    public static void RefuseNonHumanTransport(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || RequestCredentialCatalog.HasExplicitCredential(context.Request)
            || string.IsNullOrEmpty(context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName])
            || AdminSessionCrossSiteRequestGuard.IsCrossSiteUnsafeRequest(context.Request))
        {
            throw new HumanApprovalRequiredException();
        }
    }

    private async Task<VerifiedHumanSession> LoadHumanAsync(
        string? sessionId, HumanApprovalTarget target, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var session = await services.GetRequiredService<AdminAuthSessionStore>()
            .GetInteractiveSessionForApprovalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.Claims.Length == 0) throw new HumanApprovalRequiredException();

        services.GetService<RequestTenantContext>()?.Set(target.TenantId, TenantContextSource.Claim);
        var principal = AdminAuthClaimsProjector.CreatePrincipal(session.Claims, OidcAuthenticationExtensions.AdminSessionScheme);
        principal = await services.GetRequiredService<IClaimsTransformation>().TransformAsync(principal).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(principal);
        var authorization = new OperationApprovalAuthorization(
            services.GetRequiredService<IPermissionResolver>(), services.GetRequiredService<IAuthorizationService>());
        if (actor is not { IsDurablyRevalidatable: true }
            || !await authorization.CanDecideAsync(principal, cancellationToken).ConfigureAwait(false)
            || !target.PermitsScope(principal))
        {
            throw new HumanApprovalRequiredException();
        }

        return new VerifiedHumanSession(
            actor.ActorId, Hash(Encoding.UTF8.GetBytes(sessionId!)),
            Hash(JsonSerializer.SerializeToUtf8Bytes(session.Claims,
                AdminAuthSessionStoreJsonContext.Default.AdminAuthSessionClaimArray)), session.ExpiresAt);
    }

    private static void ValidateBoundProposal(OperationProposal proposal)
    {
        if (proposal.Status != OperationProposalStatus.AwaitingApproval)
            throw new InvalidOperationException("The proposal is no longer awaiting human approval.");
        if (proposal.Initiation is not { Version: OperationInitiationEvidence.CurrentVersion } initiation)
            throw new InvalidOperationException("This proposal requires a fresh proposal with versioned initiation evidence before human approval.");
        if (proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)
            || initiation.PlanHash != proposal.SealedPlanHash
            || initiation.PrincipalId != proposal.RequestedBy
            || initiation.TenantId != proposal.TenantId
            || proposal.GuardrailDecision is null
            || initiation.PolicyRevision != OperationGateway.ComputePolicyRevision(proposal.GuardrailDecision))
        {
            throw new HumanApprovalRequiredException();
        }
    }

    private OperationHumanApprovalCapability ReadCapability(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > MaximumCapabilityLength) throw new HumanApprovalRequiredException();
        try
        {
            return JsonSerializer.Deserialize(_protector.Unprotect(token),
                OperationHumanApprovalJsonContext.Default.OperationHumanApprovalCapability)
                ?? throw new HumanApprovalRequiredException();
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException)
        {
            throw new HumanApprovalRequiredException();
        }
    }

    private string ResolveAudience() => BaseUrlResolver.TryGetConfiguredBaseUrl(configuration, out var audience)
        ? audience
        : throw new ServiceUnavailableException("Configure Public:BaseUrl to enable human approval.");

    internal static string ComputeBindingDigest(OperationProposal proposal) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        OperationHumanApprovalBinding.From(proposal), OperationHumanApprovalJsonContext.Default.OperationHumanApprovalBinding));

    internal static string ComputeInitiationDigest(OperationInitiationEvidence initiation) => Hash(
        JsonSerializer.SerializeToUtf8Bytes(OperationInitiationEvidenceV1.From(initiation),
            OperationHumanApprovalJsonContext.Default.OperationInitiationEvidenceV1));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record HumanApprovalTarget(string Kind, string Id, long Version, string? TenantId,
        string BindingDigest, DateTimeOffset? ExpiresAt, Func<ClaimsPrincipal, bool> PermitsScope);
    private sealed record HumanApprovalGrant(DateTimeOffset ExpiresAt, string Capability);
    private sealed record VerifiedHumanDecision(string ActorId, string SessionReference,
        string CapabilityReference, DateTimeOffset ApprovedAt);
    private sealed record VerifiedHumanSession(string ActorId, string SessionReference, string ClaimsDigest, DateTimeOffset ExpiresAt);
}

internal sealed class HumanApprovalRequiredException() : InvalidOperationException(
    "Human approval requires a live interactive OIDC session and a valid review capability. Open the proposal in Console; MCP tokens and API keys cannot approve it.");

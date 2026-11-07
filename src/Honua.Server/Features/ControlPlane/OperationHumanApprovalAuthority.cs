// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.ControlPlane;

internal sealed class OperationHumanApprovalAuthority(
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
        if (!ReferenceEquals(context, requestContext.HttpContext)
            || context.GetEndpoint()?.Metadata.GetMetadata<AdminHumanApprovalEndpointMetadata>() != AdminHumanApprovalEndpointMetadata.Review
            || !HttpMethods.IsGet(context.Request.Method))
        {
            throw new HumanApprovalRequiredException();
        }
        RefuseNonHumanTransport(context);
        ValidateBoundProposal(proposal);
        var tenant = context.RequestServices.GetService<Honua.Core.Features.MultiTenancy.Abstractions.ITenantContext>()?.TenantId;
        if (!string.Equals(proposal.TenantId, tenant, StringComparison.Ordinal))
        {
            throw new HumanApprovalRequiredException();
        }

        var human = await LoadHumanAsync(
            context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName], proposal, context.RequestAborted)
            .ConfigureAwait(false);
        if (CanonicalSecurityActor.Resolve(context.User)?.ActorId != human.ActorId) throw new HumanApprovalRequiredException();
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + CapabilityLifetime;
        if (human.ExpiresAt < expiresAt) expiresAt = human.ExpiresAt;
        if (proposal.Evidence is { } evidence && evidence.ExpiresAt < expiresAt) expiresAt = evidence.ExpiresAt;
        if (expiresAt <= now) throw new HumanApprovalRequiredException();

        var capability = new OperationHumanApprovalCapability
        {
            Version = OperationHumanApprovalEvidence.CurrentVersion,
            Decision = "approve",
            Audience = ResolveAudience(),
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            BindingDigest = ComputeBindingDigest(proposal),
            ActorId = human.ActorId,
            TenantId = proposal.TenantId,
            SessionReference = human.SessionReference,
            ClaimsDigest = human.ClaimsDigest,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            IssuedAt = now,
            ExpiresAt = expiresAt,
        };
        return new OperationHumanApprovalReview
        {
            ProposalId = proposal.ProposalId,
            ProposalVersion = proposal.Version,
            PlanHash = proposal.SealedPlanHash!,
            ExpiresAt = expiresAt,
            Capability = _protector.Protect(JsonSerializer.Serialize(
                capability, OperationHumanApprovalJsonContext.Default.OperationHumanApprovalCapability)),
        };
    }

    public async Task<OperationHumanApprovalEvidence> VerifyAsync(
        OperationProposal proposal, OperationProposalApprovalContext approval, CancellationToken cancellationToken = default)
    {
        var context = requestContext.HttpContext ?? throw new HumanApprovalRequiredException();
        if (context.GetEndpoint()?.Metadata.GetMetadata<AdminHumanApprovalEndpointMetadata>() != AdminHumanApprovalEndpointMetadata.Approve
            || !HttpMethods.IsPost(context.Request.Method))
        {
            throw new HumanApprovalRequiredException();
        }
        RefuseNonHumanTransport(context);
        ValidateBoundProposal(proposal);
        var tokens = context.Request.Headers[CapabilityHeader];
        if (tokens.Count != 1) throw new HumanApprovalRequiredException();
        var capability = ReadCapability(tokens[0]);
        var now = timeProvider.GetUtcNow();
        if (capability.Version != OperationHumanApprovalEvidence.CurrentVersion
            || capability.Decision != "approve"
            || capability.ExpiresAt <= now
            || capability.IssuedAt > now
            || capability.ExpiresAt - capability.IssuedAt > CapabilityLifetime
            || capability.Audience != ResolveAudience()
            || capability.ProposalId != proposal.ProposalId
            || capability.ProposalVersion != proposal.Version
            || capability.BindingDigest != ComputeBindingDigest(proposal)
            || capability.TenantId != proposal.TenantId
            || capability.TenantId != (string.IsNullOrEmpty(approval.TenantId) ? null : approval.TenantId))
        {
            throw new HumanApprovalRequiredException();
        }

        var human = await LoadHumanAsync(
            context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName], proposal, cancellationToken).ConfigureAwait(false);
        if (capability.ActorId != human.ActorId || capability.ActorId != approval.ApprovedBy
            || CanonicalSecurityActor.Resolve(context.User)?.ActorId != human.ActorId
            || capability.SessionReference != human.SessionReference || capability.ClaimsDigest != human.ClaimsDigest
            || capability.ExpiresAt > human.ExpiresAt
            || (proposal.Evidence is { } evidence && capability.ExpiresAt > evidence.ExpiresAt))
        {
            throw new HumanApprovalRequiredException();
        }

        return new OperationHumanApprovalEvidence
        {
            Version = OperationHumanApprovalEvidence.CurrentVersion,
            ActorId = human.ActorId,
            Channel = OperationHumanApprovalEvidence.InteractiveOidcChannel,
            SessionReference = human.SessionReference,
            CapabilityReference = Hash(Encoding.UTF8.GetBytes(capability.Nonce)),
            PlanHash = proposal.SealedPlanHash!,
            InitiationDigest = Hash(JsonSerializer.SerializeToUtf8Bytes(
                proposal.Initiation!, OperationProposalJsonContext.Default.OperationInitiationEvidence)),
            ProposalVersion = proposal.Version,
            ApprovedAt = now,
        };
    }

    public static void RefuseNonHumanTransport(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
            || context.Request.Headers.ContainsKey("Authorization")
            || context.Request.Headers.ContainsKey("X-API-Key")
            || context.Request.Query.ContainsKey("token")
            || string.IsNullOrEmpty(context.Request.Cookies[AdminAuthSessionStore.AuthSessionCookieName])
            || AdminSessionCrossSiteRequestGuard.IsCrossSiteUnsafeRequest(context.Request))
        {
            throw new HumanApprovalRequiredException();
        }
    }

    private async Task<VerifiedHumanSession> LoadHumanAsync(
        string? sessionId, OperationProposal proposal, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var session = await services.GetRequiredService<AdminAuthSessionStore>()
            .GetInteractiveSessionForApprovalAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || session.Claims.Length == 0) throw new HumanApprovalRequiredException();

        services.GetService<RequestTenantContext>()?.Set(proposal.TenantId, TenantContextSource.Claim);
        var principal = AdminAuthClaimsProjector.CreatePrincipal(session.Claims, OidcAuthenticationExtensions.AdminSessionScheme);
        principal = await services.GetRequiredService<IClaimsTransformation>().TransformAsync(principal).ConfigureAwait(false);
        var actor = CanonicalSecurityActor.Resolve(principal);
        var authorization = new OperationApprovalAuthorization(
            services.GetRequiredService<IPermissionResolver>(), services.GetRequiredService<IAuthorizationService>());
        if (actor is not { IsDurablyRevalidatable: true }
            || !await authorization.CanDecideAsync(principal, cancellationToken).ConfigureAwait(false)
            || !OperationApprovalAuthorization.PermitsOperationScope(principal, proposal))
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

    private static string ComputeBindingDigest(OperationProposal proposal) => Hash(JsonSerializer.SerializeToUtf8Bytes(
        OperationHumanApprovalBinding.From(proposal), OperationHumanApprovalJsonContext.Default.OperationHumanApprovalBinding));

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record VerifiedHumanSession(string ActorId, string SessionReference, string ClaimsDigest, DateTimeOffset ExpiresAt);
}

internal sealed class HumanApprovalRequiredException() : InvalidOperationException(
    "Human approval requires a live interactive session and a valid review capability. Open the proposal in Console or follow the MCP human-review link.");

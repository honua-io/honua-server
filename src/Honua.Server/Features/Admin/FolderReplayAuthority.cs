// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal sealed record FolderReplayAdmission
{
    public required ClaimsPrincipal Principal { get; init; }
    public required string OperationId { get; init; }
    public required string ProposalId { get; init; }
    public required string OperationInstanceId { get; init; }
    public string? TenantId { get; init; }
}

internal sealed class FolderReplayAuthority
{
    private FolderReplayAuthority(ClaimsPrincipal principal, OperationProposal proposal)
    {
        var identity = new ClaimsIdentity(principal.Identity as ClaimsIdentity);
        foreach (var role in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(role);
        RequesterPrincipal = new ClaimsPrincipal(new ClaimsIdentity(identity));
        // Approval delegates operator authority without granting service-policy roles.
        identity.AddClaim(new Claim(ClaimTypes.Role, OperationPolicyBuiltInRules.FullAdministratorRole));
        OperatorPrincipal = new ClaimsPrincipal(identity);
        Proposal = proposal;
    }

    internal ClaimsPrincipal RequesterPrincipal { get; }
    internal ClaimsPrincipal OperatorPrincipal { get; }
    internal OperationProposal Proposal { get; }

    internal static async Task<FolderReplayAuthority?> AuthorizeAsync(
        FolderReplayAdmission admission, IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var principal = admission.Principal;
        if (principal.Identity?.IsAuthenticated != true ||
            !FolderOperationCredential.IsFolderMutation(admission.OperationId) ||
            !principal.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            string.IsNullOrWhiteSpace(admission.ProposalId) ||
            !principal.HasClaim(AdminApiKeyPermission.PermissionClaimType, FolderOperationCredential.ProposalGrant(admission.ProposalId)))
            return null;
        var proposal = await services.GetRequiredService<IOperationProposalStore>()
            .GetAsync(admission.ProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.ProposalId != admission.ProposalId || proposal.OperationId != admission.OperationId ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId, admission.TenantId) ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId, principal.FindFirstValue(AdminApiKeyPermission.ApprovedOperationTenantClaim)) ||
            !CredentialMatchesPlan(principal, proposal)) return null;
        var verifier = services.GetRequiredService<IOperationApprovalReplayVerifier>();
        return await verifier.VerifyAsync(admission.ProposalId, admission.OperationInstanceId,
            proposal.SealedPlanHash ?? string.Empty, cancellationToken).ConfigureAwait(false)
            ? new FolderReplayAuthority(principal, proposal) : null;
    }

    private static bool CredentialMatchesPlan(ClaimsPrincipal principal, OperationProposal proposal)
    {
        try
        {
            var request = FolderReplayRequest.ReadSealedRequest(proposal.Plan, proposal.OperationId, proposal.TenantId).Request;
            var definition = AdminApiOperationCatalog.Definitions.Single(item => item.OperationId == request.OperationId);
            var path = "/api/v1/admin" + AdminApiOperationExecutor.BindPath(definition, request);
            return AdminApiKeyPermission.IsAuthorized(principal, definition.Method.Method, path);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

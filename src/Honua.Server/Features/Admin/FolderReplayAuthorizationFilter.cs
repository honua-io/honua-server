// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal sealed class FolderReplayAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole))
            return await next(invocation).ConfigureAwait(false);
        var proposal = await ApprovedReplayAsync(invocation).ConfigureAwait(false);
        if (proposal is null)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Folder operation replay authorization failed. Submit a fresh proposal.");
        var transportPrincipal = context.User;
        var identity = new ClaimsIdentity(transportPrincipal.Identity as ClaimsIdentity);
        foreach (var role in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(role);
        context.Features.Set(new FolderReplayServicePrincipal(new ClaimsPrincipal(new ClaimsIdentity(identity)), proposal));
        // The sealed proposal delegates operator authority, never service-policy grants.
        identity.AddClaim(new Claim(ClaimTypes.Role, OperationPolicyBuiltInRules.FullAdministratorRole));
        context.User = new ClaimsPrincipal(identity);
        try
        {
            return await next(invocation).ConfigureAwait(false);
        }
        finally
        {
            context.User = transportPrincipal;
            context.Features.Set<FolderReplayServicePrincipal>(null);
        }
    }

    private static async Task<OperationProposal?> ApprovedReplayAsync(EndpointFilterInvocationContext invocation)
    {
        var context = invocation.HttpContext;
        var proposalId = context.Request.Headers[OperationLineageHeaders.ProposalId].ToString();
        if (context.User.Identity?.IsAuthenticated != true ||
            !context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            string.IsNullOrWhiteSpace(proposalId) ||
            OperationId(context.Request.Method) is not { } operationId ||
            !context.User.HasClaim(AdminApiKeyPermission.PermissionClaimType, FolderOperationCredential.ProposalGrant(proposalId)) ||
            !AdminApiKeyPermission.IsAuthorized(context.User, context.Request.Method, context.Request.Path.Value))
            return null;
        var proposal = await context.RequestServices.GetRequiredService<IOperationProposalStore>()
            .GetAsync(proposalId, context.RequestAborted).ConfigureAwait(false);
        if (proposal is null || proposal.OperationId != operationId ||
            !FolderReplayRequest.Matches(invocation, proposal) ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId,
                context.User.FindFirstValue(AdminApiKeyPermission.ApprovedOperationTenantClaim)) ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId,
                context.RequestServices.GetRequiredService<ITenantContext>().TenantId))
            return null;
        var verifier = context.RequestServices.GetRequiredService<IOperationApprovalReplayVerifier>();
        return await verifier.VerifyAsync(proposalId,
            context.Request.Headers[OperationLineageHeaders.OperationInstanceId].ToString(),
            proposal.SealedPlanHash ?? string.Empty, context.RequestAborted).ConfigureAwait(false) ? proposal : null;
    }

    private static string? OperationId(string method) => method switch
    {
        "POST" => "folder.create",
        "PATCH" => "folder.update",
        "DELETE" => "folder.delete",
        "PUT" => "service.move",
        _ => null
    };
}

internal sealed record FolderReplayServicePrincipal(ClaimsPrincipal Principal, OperationProposal Proposal);

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Admin;

internal sealed class FolderReplayAuthorizationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        if (!await IsApprovedReplayAsync(context).ConfigureAwait(false))
            return await next(invocation).ConfigureAwait(false);
        var transportPrincipal = context.User;
        var identity = new ClaimsIdentity(transportPrincipal.Identity as ClaimsIdentity);
        foreach (var role in identity.FindAll(ClaimTypes.Role).ToArray()) identity.RemoveClaim(role);
        // The sealed proposal delegates operator authority only inside its folder endpoint.
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        context.User = new ClaimsPrincipal(identity);
        try
        {
            return await next(invocation).ConfigureAwait(false);
        }
        finally
        {
            context.User = transportPrincipal;
        }
    }

    private static async Task<bool> IsApprovedReplayAsync(HttpContext context)
    {
        var proposalId = context.Request.Headers[OperationLineageHeaders.ProposalId].ToString();
        if (context.User.Identity?.IsAuthenticated != true ||
            !context.User.IsInRole(AdminApiKeyPermission.ApprovedOperationRole) ||
            string.IsNullOrWhiteSpace(proposalId) ||
            OperationId(context.Request.Method) is not { } operationId ||
            !AdminApiKeyPermission.IsAuthorized(context.User, context.Request.Method, context.Request.Path.Value))
            return false;
        var proposal = await context.RequestServices.GetRequiredService<IOperationProposalStore>()
            .GetAsync(proposalId, context.RequestAborted).ConfigureAwait(false);
        if (proposal is null || proposal.OperationId != operationId ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId,
                context.User.FindFirstValue(AdminApiKeyPermission.ApprovedOperationTenantClaim)) ||
            !MetadataV2ServicePaths.SameTenant(proposal.TenantId,
                context.RequestServices.GetRequiredService<ITenantContext>().TenantId))
            return false;
        var verifier = context.RequestServices.GetRequiredService<IOperationApprovalReplayVerifier>();
        return await verifier.VerifyAsync(proposalId,
            context.Request.Headers[OperationLineageHeaders.OperationInstanceId].ToString(),
            proposal.SealedPlanHash ?? string.Empty, context.RequestAborted).ConfigureAwait(false);
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

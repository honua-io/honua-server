// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
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
        var authority = await ApprovedReplayAsync(invocation).ConfigureAwait(false);
        if (authority is null)
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, detail: "Folder operation replay authorization failed. Submit a fresh proposal.");
        var transportPrincipal = context.User;
        context.Features.Set(new FolderReplayServicePrincipal(authority.RequesterPrincipal, authority.Proposal,
            new FolderReplayAdmission
            {
                Principal = transportPrincipal,
                OperationId = authority.Proposal.OperationId!,
                ProposalId = context.Request.Headers[OperationLineageHeaders.ProposalId].ToString(),
                OperationInstanceId = context.Request.Headers[OperationLineageHeaders.OperationInstanceId].ToString(),
                TenantId = context.RequestServices.GetRequiredService<ITenantContext>().TenantId
            }));
        context.User = authority.OperatorPrincipal;
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

    private static async Task<FolderReplayAuthority?> ApprovedReplayAsync(EndpointFilterInvocationContext invocation)
    {
        var context = invocation.HttpContext;
        if (context.GetEndpoint()?.Metadata.GetMetadata<AdminApiOperationCatalog.Definition>() is not { Destructive: true } definition)
            return null;
        var authority = await FolderReplayAuthority.AuthorizeAsync(new FolderReplayAdmission
        {
            Principal = context.User,
            OperationId = definition.OperationId,
            ProposalId = context.Request.Headers[OperationLineageHeaders.ProposalId].ToString(),
            OperationInstanceId = context.Request.Headers[OperationLineageHeaders.OperationInstanceId].ToString(),
            TenantId = context.RequestServices.GetRequiredService<ITenantContext>().TenantId
        }, context.RequestServices, context.RequestAborted).ConfigureAwait(false);
        return authority is not null && FolderReplayRequest.Matches(invocation, authority.Proposal) ? authority : null;
    }
}

internal sealed record FolderReplayServicePrincipal(
    ClaimsPrincipal Principal, OperationProposal Proposal, FolderReplayAdmission? Admission = null);

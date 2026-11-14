// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Server.Features.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Admin;

internal static class AgentAuthorityEndpoints
{
    public static void MapAgentAuthorityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v{version:apiVersion}/admin/agent-authority")
            .WithMetadata(TenantIndependentControlPlaneMetadata.Instance)
            .WithApiVersionSet().HasApiVersion(1, 0)
            .WithTags("Admin", "Agent Authority").RequireAdminAuthorization();
        group.MapGet("/", GetAsync).WithDisplayName("Get Agent Authority Ceiling");
        group.MapPut("/", SetAsync).WithDisplayName("Set Agent Authority Ceiling")
            .AddEndpointFilter(AgentApprovalFilter.AccessGrants);
        group.MapDelete("/", DeleteAsync).WithDisplayName("Delete Agent Authority Ceiling")
            .AddEndpointFilter(AgentApprovalFilter.AccessGrants);
    }

    internal sealed record AuthorityQuery(string PrincipalId, string? TenantId);

    private static async Task<IResult> GetAsync([AsParameters] AuthorityQuery query,
        [FromServices] IAgentAuthorityStore store, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(query.PrincipalId))
            return TypedResults.BadRequest();
        var ceiling = await store.GetAsync(query.TenantId, query.PrincipalId, context.RequestAborted).ConfigureAwait(false);
        return ceiling is null ? TypedResults.NotFound() : TypedResults.Json(ceiling,
            SharedConfigurationJsonContext.Default.AgentAuthorityCeiling);
    }

    private static async Task<IResult> SetAsync(AgentAuthorityCeiling ceiling,
        [FromServices] IAgentAuthorityStore store, HttpContext context)
    {
        if (!ceiling.IsValid())
            return TypedResults.BadRequest();
        await store.SetAsync(ceiling, context.RequestAborted).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> DeleteAsync([AsParameters] AuthorityQuery query,
        [FromServices] IAgentAuthorityStore store, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(query.PrincipalId))
            return TypedResults.BadRequest();
        return await store.DeleteAsync(query.TenantId, query.PrincipalId, context.RequestAborted).ConfigureAwait(false)
            ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}

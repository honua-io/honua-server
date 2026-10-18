// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;

namespace Honua.Import.FileImport;

/// <summary>Applies current operator authority and persisted ownership to import observations and cancellation.</summary>
internal static class ImportOperationAuthorization
{
    internal static ImportOperationOwnership? Capture(HttpContext context)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        return actor is null ? null : new(actor, context.RequestServices.GetService<ITenantContext>()?.TenantId);
    }

    internal static async Task<bool> CanAccessAsync(HttpContext context, ImportOperationOwnership? ownership,
        CancellationToken cancellationToken)
    {
        if (!await OperationAdminAuthorization.IsAuthorizedAsync(context, context.User,
                OperationSideEffectClass.ReadOnly, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId;
        if (actor is null)
        {
            return false;
        }

        var agent = context.User.HasClaim("permission", "agent:workspace");
        if (!agent)
        {
            return OperationTenantAuthorization.CanAccess(context, ownership?.TenantId);
        }

        // Agent workload roles never confer a cross-tenant operator exemption. Unknown
        // legacy ownership cannot be inferred from a supplied job ID or default tenant.
        var tenant = context.RequestServices.GetService<ITenantContext>()?.TenantId;
        if (ownership is null || string.IsNullOrWhiteSpace(tenant) || ownership.TenantId != tenant)
        {
            return false;
        }

        // A workspace header alone cannot delegate an unrelated import. Cross-actor
        // recovery requires an immutable task/resource binding on the accepted job.
        return ownership.OwnerActorId == actor;
    }
}

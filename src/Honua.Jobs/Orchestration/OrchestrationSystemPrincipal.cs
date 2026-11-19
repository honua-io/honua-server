// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Orchestration;

internal static class OrchestrationSystemPrincipal
{
    public const string SystemIdentityName = "honua/orchestrator";
    public const string AuthenticationType = "HonuaOrchestrator";

    public static ClaimsPrincipal Create(string? requestedBy, JobSecurityContext? securityContext = null)
    {
        var name = string.IsNullOrWhiteSpace(requestedBy) ? SystemIdentityName : requestedBy;
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, "orchestrator"),
                new Claim(ClaimTypes.Role, "admin")
            },
            AuthenticationType);
        // Submission pins this durable tenant on the child job. Reconciliation runs
        // without request middleware, so observation/results/cancel must carry the same
        // tenant rather than treating the internal admin role as a cross-tenant bypass.
        if (!string.IsNullOrWhiteSpace(securityContext?.TenantId))
        {
            identity.AddClaim(new Claim("tenant_id", securityContext.TenantId));
        }

        return new ClaimsPrincipal(identity);
    }

    // The identity a run uses to submit, observe and cancel its step jobs. A run without its
    // requester's snapshot gets no administrative role, so it cannot reach any job id recorded
    // in its step states; the engine ends such a run instead of acting for it.
    public static ClaimsPrincipal ForRun(OperationAuditInfo runAudit)
    {
        if (runAudit.SubmitterSecurityContext is { } snapshot)
        {
            return Create(runAudit.RequestedBy, snapshot);
        }

        var name = string.IsNullOrWhiteSpace(runAudit.RequestedBy) ? SystemIdentityName : runAudit.RequestedBy;
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, "orchestrator")],
            AuthenticationType));
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Synthesizes <see cref="ClaimsPrincipal"/> instances for orchestration-initiated
/// job submissions. The engine needs a principal to flow through the workflow job
/// executor substrate; a run either forwards the original operator identity captured
/// at creation time or falls back to a system identity for cron-driven runs.
/// </summary>
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
}

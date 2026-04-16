// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Synthesizes <see cref="ClaimsPrincipal"/> instances for orchestration-initiated
/// job submissions. The engine needs a principal to flow through
/// <see cref="Features.Geoprocessing.IGeoprocessingJobService"/>; a run either forwards the
/// original operator identity captured at creation time or falls back to a system identity
/// for cron-driven runs.
/// </summary>
internal static class OrchestrationSystemPrincipal
{
    public const string SystemIdentityName = "honua/orchestrator";
    public const string AuthenticationType = "HonuaOrchestrator";

    public static ClaimsPrincipal Create(string? requestedBy)
    {
        var name = string.IsNullOrWhiteSpace(requestedBy) ? SystemIdentityName : requestedBy;
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.Name, name),
                new Claim(ClaimTypes.Role, "orchestrator")
            },
            AuthenticationType);
        return new ClaimsPrincipal(identity);
    }
}

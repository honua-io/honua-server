// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>Captures only the current host-authenticated agent request and its middleware-selected tenant.</summary>
internal sealed class AgentJobSecurityContextCapture(IHttpContextAccessor accessor) : IAgentJobSecurityContextCapture
{
    public async Task<JobSecurityContext> CaptureCurrentAsync(CancellationToken cancellationToken = default)
    {
        var http = accessor.HttpContext
            ?? throw new UnauthorizedAccessException("Agent policy capture requires an authenticated current request.");
        var services = http.RequestServices;
        var result = await AgentJobSecurityBridge.CaptureAsync(http.User,
            services.GetRequiredService<IOptions<RbacOptions>>().Value,
            services, services.GetService<ITenantContext>(), cancellationToken).ConfigureAwait(false);
        _ = JobSecurityContextFingerprint.Compute(result);
        return result;
    }
}

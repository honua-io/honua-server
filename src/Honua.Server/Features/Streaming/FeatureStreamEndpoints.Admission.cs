// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.RateLimiting;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    internal static string ResolveAdmissionPartition(HttpContext context)
    {
        // Anonymous callers are classified first, so a default tenant for public reads cannot pool
        // them with that tenant's users. Each client address (an IPv6 /64, after forwarded-header
        // processing) has its own small allowance; all anonymous clients together share one more.
        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is null)
        {
            return FeatureStreamSessionManager.AnonymousClientPartitionPrefix
                + (ClientAddressPartition.From(context.Connection.RemoteIpAddress) ?? "unknown");
        }

        // Single-tenant deployments resolve every request to the deployment tenant, so a
        // tenant partition there would let one principal exhaust the whole node.
        var tenancyEnabled = context.RequestServices.GetService<IOptions<TenantContextOptions>>()?.Value.Enabled == true;
        var tenant = tenancyEnabled ? context.RequestServices.GetService<ITenantContext>()?.TenantId : null;
        return string.IsNullOrWhiteSpace(tenant) ? $"principal:{actor.ActorId}" : $"tenant:{tenant}";
    }
}

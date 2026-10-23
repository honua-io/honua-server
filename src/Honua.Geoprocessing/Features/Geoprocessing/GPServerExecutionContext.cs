// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.Geoprocessing;

/// <summary>Server-resolved GP route and canonical catalog identity, never request parameters.</summary>
internal sealed record GPServerExecutionContext(string ServiceId, string TaskName, string ProcessId)
{
    /// <summary>Matches only an authenticated scoped API key's exact service/process execute grant.</summary>
    public bool IsGranted(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || !principal.HasClaim(claim => claim.Type == "api_key_id" && Guid.TryParse(claim.Value, out _))
            || !principal.HasClaim("auth_type", "admin-api-key") && !principal.HasClaim("auth_type", "layer-write-key")
            || !principal.IsInRole("scoped-api-key") && !principal.IsInRole("layer-write-key"))
        {
            return false;
        }

        return LayerScopedWriteKey.AllowsGPExecution(principal, ServiceId, ProcessId);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;

namespace Honua.Geoprocessing;

// The route and catalog identity are resolved by the server and are never taken from request parameters.
internal sealed record GPServerExecutionContext(string ServiceId, string TaskName, string ProcessId)
{
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

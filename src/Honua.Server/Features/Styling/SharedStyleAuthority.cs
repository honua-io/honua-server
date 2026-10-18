// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Styling;

internal static class SharedStyleAuthority
{
    internal static async Task RequireCurrentKeyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!await AgentCredentialAuthority.IsCurrentAsync(context.User, context.RequestServices, cancellationToken).ConfigureAwait(false)) Deny();
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny() => throw new GeoprocessingAuthorizationException(requiresAuthentication: false);
}

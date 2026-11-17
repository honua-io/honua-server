// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Identity.Scim;

internal static partial class ScimEndpoints
{
    private static ValueTask<object?> RequireScimAuthentication(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var rejection = AuthenticateWithAttemptLimit(invocationContext.HttpContext);
        return rejection is null
            ? next(invocationContext)
            : ValueTask.FromResult<object?>(rejection);
    }

    private static IResult? AuthenticateWithAttemptLimit(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<ScimProvisioningOptions>>().Value;
        var gate = context.RequestServices.GetRequiredService<AuthenticationAttemptGate>();
        using var attempt = gate.Begin(context.Connection.RemoteIpAddress, context.Request.Headers.Authorization.ToString());
        if (attempt.RetryAfter is { } retryAfter)
        {
            return TooManyAttemptsResponse.Create(context, retryAfter);
        }

        if (Authenticate(context, options))
        {
            return null;
        }

        attempt.CountFailure();
        return Unauthorized();
    }
}

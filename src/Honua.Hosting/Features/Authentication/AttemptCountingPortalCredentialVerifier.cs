// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

// The admin verifier compares the administrator password and managed keys, so an unverified exchange counts and a refused source is never compared.
internal sealed class AttemptCountingPortalCredentialVerifier(
    IPortalCredentialVerifier inner,
    AuthenticationAttemptGate gate,
    IHttpContextAccessor httpContextAccessor) : IPortalCredentialVerifier
{
    public async Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        using var attempt = gate.Begin(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress, password);
        if (attempt.RetryAfter is not null)
        {
            return null;
        }

        var principal = await inner.VerifyAsync(username, password, cancellationToken).ConfigureAwait(false);
        if (principal is null)
        {
            attempt.CountFailure();
        }

        return principal;
    }
}

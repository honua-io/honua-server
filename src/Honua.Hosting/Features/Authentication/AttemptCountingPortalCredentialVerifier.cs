// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

// A refused source is checked before the store; only the fallback password comparison reserves an in-flight slot.
internal sealed class AttemptCountingPortalCredentialVerifier(
    AdminPortalCredentialVerifier inner,
    AuthenticationAttemptGate gate,
    IHttpContextAccessor httpContextAccessor) : IPortalCredentialVerifier
{
    public Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
        => inner.VerifyAsync(
            new PortalCredentialAttempt(username, password, httpContextAccessor.HttpContext?.Connection.RemoteIpAddress),
            gate,
            cancellationToken);
}

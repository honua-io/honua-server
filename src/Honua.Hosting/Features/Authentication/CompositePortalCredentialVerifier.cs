// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

internal sealed class CompositePortalCredentialVerifier : IPortalCredentialVerifier
{
    private readonly IReadOnlyList<IPortalCredentialVerifier> _verifiers;

    public CompositePortalCredentialVerifier(params IPortalCredentialVerifier[] verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        _verifiers = verifiers;
    }

    public async Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        foreach (var verifier in _verifiers)
        {
            var result = await verifier.VerifyAsync(username, password, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }
}

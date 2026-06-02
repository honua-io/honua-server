// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Chains <see cref="IPortalCredentialVerifier"/> implementations, returning the
/// first non-null projection (#1370). Used when the OIDC-backed verifier is enabled
/// with admin fallback so a named-user OIDC token <i>and</i> an admin password /
/// admin API key can both authenticate against <c>/sharing/rest/generateToken</c>.
/// </summary>
internal sealed class CompositePortalCredentialVerifier : IPortalCredentialVerifier
{
    private readonly IReadOnlyList<IPortalCredentialVerifier> _verifiers;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositePortalCredentialVerifier"/> class.
    /// </summary>
    /// <param name="verifiers">The verifiers to consult in order; the first that
    /// returns a non-null principal wins.</param>
    public CompositePortalCredentialVerifier(params IPortalCredentialVerifier[] verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        _verifiers = verifiers;
    }

    /// <inheritdoc />
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

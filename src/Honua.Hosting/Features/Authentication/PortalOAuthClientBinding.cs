// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

internal enum PortalOAuthClientDecision
{
    Allowed = 0,
    UnknownClient = 1,
    InactiveClient = 2,
    GrantNotAllowed = 3,
    RedirectNotRegistered = 4,
}

// The named-user flow (authorize, code exchange, refresh) honours a registered
// client's own redirect URIs, grant types, expiry and deletion. A client_id
// with no registration is a client-chosen label (desktop and mobile apps send
// their own app id) and keeps the deployment redirect allow-list alone.
internal static class PortalOAuthClientBinding
{
    private const string AuthorizationCodeGrant = "authorization_code";
    private const string RefreshTokenGrant = "refresh_token";

    public static async Task<PortalOAuthClientDecision> AuthorizeAsync(
        IOAuthClientStore clients,
        string clientId,
        string? redirectUri,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return PortalOAuthClientDecision.Allowed;
        }

        var client = await clients.FindByClientIdAsync(clientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return OAuthClientIdentifiers.HasRegistryShape(clientId)
                ? PortalOAuthClientDecision.UnknownClient
                : PortalOAuthClientDecision.Allowed;
        }

        if (client.RevokedAt is not null
            || (client.ExpiresAt.HasValue && client.ExpiresAt.Value <= timeProvider.GetUtcNow()))
        {
            return PortalOAuthClientDecision.InactiveClient;
        }

        if (!AllowsNamedUserGrant(client))
        {
            return PortalOAuthClientDecision.GrantNotAllowed;
        }

        // redirect_uri is null on code exchange and refresh: the code and the refresh
        // token are already bound to the exact redirect checked at authorize.
        if (redirectUri is not null && !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return PortalOAuthClientDecision.RedirectNotRegistered;
        }

        return PortalOAuthClientDecision.Allowed;
    }

    // No registered grant types means unrestricted (as for client_credentials).
    // A refresh token only comes from the code flow, so either label admits both.
    private static bool AllowsNamedUserGrant(OAuthClientRecord client)
        => client.AllowedGrantTypes.Count == 0
            || client.AllowedGrantTypes.Contains(AuthorizationCodeGrant, StringComparer.OrdinalIgnoreCase)
            || client.AllowedGrantTypes.Contains(RefreshTokenGrant, StringComparer.OrdinalIgnoreCase);
}

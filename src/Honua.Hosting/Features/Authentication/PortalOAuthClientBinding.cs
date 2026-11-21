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

internal enum PortalOAuthNamedUserStep
{
    Authorize = 0,
    CodeExchange = 1,
    Refresh = 2,
}

// RedirectUri is checked only at authorize: the code and the refresh token are
// already bound to the exact redirect accepted there.
internal sealed record PortalOAuthClientCheck(string ClientId, PortalOAuthNamedUserStep Step, string? RedirectUri = null);

// The named-user flow (authorize, code exchange, refresh) honours a registered
// client's own redirect URIs, grant types, expiry and deletion. A client_id
// with no registration is a client-chosen label (desktop and mobile apps send
// their own app id) and keeps the deployment redirect allow-list alone.
internal sealed class PortalOAuthClientBinding(IOAuthClientStore clients, TimeProvider timeProvider)
{
    private const string AuthorizationCodeGrant = "authorization_code";
    private const string RefreshTokenGrant = "refresh_token";

    public async Task<PortalOAuthClientDecision> CheckAsync(PortalOAuthClientCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (string.IsNullOrWhiteSpace(check.ClientId))
        {
            return PortalOAuthClientDecision.Allowed;
        }

        var client = await clients.FindByClientIdAsync(check.ClientId, cancellationToken).ConfigureAwait(false);
        if (client is null)
        {
            return OAuthClientIdentifiers.HasRegistryShape(check.ClientId)
                ? PortalOAuthClientDecision.UnknownClient
                : PortalOAuthClientDecision.Allowed;
        }

        return Decide(client, check, timeProvider.GetUtcNow());
    }

    // The redirect is checked before the grant so that a refusal is only ever
    // redirected to one of the client's own URIs.
    private static PortalOAuthClientDecision Decide(OAuthClientRecord client, PortalOAuthClientCheck check, DateTimeOffset now)
    {
        if (client.RevokedAt is not null || (client.ExpiresAt.HasValue && client.ExpiresAt.Value <= now))
        {
            return PortalOAuthClientDecision.InactiveClient;
        }

        if (check.Step == PortalOAuthNamedUserStep.Authorize
            && (check.RedirectUri is null || !client.RedirectUris.Contains(check.RedirectUri, StringComparer.Ordinal)))
        {
            return PortalOAuthClientDecision.RedirectNotRegistered;
        }

        return AllowsGrant(client, check.Step)
            ? PortalOAuthClientDecision.Allowed
            : PortalOAuthClientDecision.GrantNotAllowed;
    }

    // No registered grant types means unrestricted (as for client_credentials).
    // Authorize and code exchange need authorization_code; refresh accepts either.
    private static bool AllowsGrant(OAuthClientRecord client, PortalOAuthNamedUserStep step)
    {
        var grants = client.AllowedGrantTypes;
        return grants.Count == 0
            || grants.Contains(AuthorizationCodeGrant, StringComparer.OrdinalIgnoreCase)
            || (step == PortalOAuthNamedUserStep.Refresh && grants.Contains(RefreshTokenGrant, StringComparer.OrdinalIgnoreCase));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

public sealed class PortalTokenAuthenticationOptions
{
    public const string SectionName = "Authentication:PortalToken";

    public const int DefaultExpirationMinutesValue = 60;

    public const int DefaultMaxExpirationMinutesValue = 14_400;

    public bool Enabled { get; set; } = true;

    public bool RequireHttps { get; set; } = true;

    public int DefaultExpirationMinutes { get; set; } = DefaultExpirationMinutesValue;

    public const int DefaultSourceRevalidationSecondsValue = 10;

    // A requested lifetime is clamped to this ceiling and to the source credential's expiry, so a short-lived key cannot mint a long-lived token.
    public int MaxExpirationMinutes { get; set; } = DefaultMaxExpirationMinutesValue;

    // This interval is also the worst-case delay before a revoked credential stops its tokens; zero re-reads on every request.
    public int SourceRevalidationSeconds { get; set; } = DefaultSourceRevalidationSecondsValue;

    public PortalOAuth2Options OAuth2 { get; set; } = new();
}

public sealed class PortalOAuth2Options
{
    public const string SectionName = "OAuth2";

    // An empty redirect allow-list rejects every redirect, and an origin entry matches only scheme, host, and port.
    public string[] AllowedRedirectUris { get; set; } = [];

    // PKCE is required unless this is turned off, which re-opens authorization-code interception.
    public bool RequirePkce { get; set; } = true;

    // Refresh tokens rotate by default; leaving them stable is only for a client that cannot store the replacement.
    public bool RotateRefreshTokens { get; set; } = true;

    // The client-credentials grant stays off unless enabled, so a deployment does not gain that path implicitly.
    public bool EnableClientCredentials { get; set; }

    public PortalOAuthJwtOptions Jwt { get; set; } = new();

    public PortalOAuthClientCredentialsFederationOptions ClientCredentialsFederation { get; set; } = new();
}

public sealed class PortalOAuthJwtOptions
{
    public const string SectionName = "Jwt";

    public const string DefaultIssuer = "https://honua.local/sharing";

    public bool EnableJwtAccessTokens { get; set; }

    // Introspection stays off unless enabled, and while off the endpoint answers 404.
    public bool EnableIntrospection { get; set; }

    // The HMAC secret is required and must be at least 32 bytes when JWT issuance is on, and an env: prefix is resolved from the environment.
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = DefaultIssuer;

    public string? Audience { get; set; }
}

// A secret a local store already accepted is never forwarded to the federated identity provider.
public sealed class PortalOAuthClientCredentialsFederationOptions
{
    public const string SectionName = "ClientCredentialsFederation";

    public bool Enabled { get; set; }

    public string? TokenEndpoint { get; set; }

    public string? DefaultScope { get; set; }

    // An empty federated role list grants nothing; it is not an escalation.
    public string[] GrantedRoles { get; set; } = [];

    // The federated token endpoint must be HTTPS unless this is turned off for an in-cluster test provider.
    public bool RequireHttps { get; set; } = true;
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Authentication;

public sealed class OidcAuthenticationOptions
{
    public const string SectionName = "Oidc";

    public bool Enabled { get; set; }

    // The admin OIDC callback must be an absolute HTTPS URI at /admin/auth/callback with no userinfo, query, or fragment; when absent the broker uses the public base URL and does not change the API origin.
    public string? AdminAuthCallbackUri { get; set; }

    internal bool TryGetAdminAuthCallbackUri(out Uri? callback)
    {
        callback = null;
        if (AdminAuthCallbackUri is null)
        {
            return true;
        }

        return string.Equals(AdminAuthCallbackUri, AdminAuthCallbackUri.Trim(), StringComparison.Ordinal)
            && Uri.TryCreate(AdminAuthCallbackUri, UriKind.Absolute, out callback)
            && callback.IsWellFormedOriginalString()
            && callback.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(callback.Host)
            && string.IsNullOrEmpty(callback.UserInfo)
            && callback.AbsolutePath == "/admin/auth/callback"
            && !AdminAuthCallbackUri.Contains('?')
            && !AdminAuthCallbackUri.Contains('#');
    }

    public AzureAdProviderOptions? AzureAd { get; set; }

    public GoogleProviderOptions? Google { get; set; }

    public GenericOidcProviderOptions? Generic { get; set; }

    public OktaProviderOptions? Okta { get; set; }

    public Auth0ProviderOptions? Auth0 { get; set; }

    public ClaimsMappingOptions ClaimsMapping { get; set; } = new();

    public string DefaultRole { get; set; } = "user";

    public string[] AdminRoles { get; set; } = ["admin", "administrator"];

    public bool RequireHttps { get; set; } = true;

    public TokenValidationOptions TokenValidation { get; set; } = new();
}

public sealed class AzureAdProviderOptions
{
    public bool Enabled { get; set; }

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    public string CallbackPath { get; set; } = "/signin-oidc-azuread";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc-azuread";

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(TenantId) &&
        !string.IsNullOrEmpty(ClientId);
}

public sealed class GoogleProviderOptions
{
    public bool Enabled { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc-google";

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(ClientId) &&
        !string.IsNullOrEmpty(ClientSecret);
}

public sealed class GenericOidcProviderOptions
{
    public bool Enabled { get; set; }

    public string DisplayName { get; set; } = "External Identity Provider";

    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public string ResponseType { get; set; } = "code";

    public bool UsePkce { get; set; } = true;

    public bool SaveTokens { get; set; } = true;

    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;

    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(Authority) &&
        !string.IsNullOrEmpty(ClientId);
}

public sealed class ClaimsMappingOptions
{
    public string NameClaimType { get; set; } = "name";

    internal const string DefaultRoleClaimType = "roles";

    public string RoleClaimType { get; set; } = DefaultRoleClaimType;

    public string EmailClaimType { get; set; } = "email";

    public string UserIdClaimType { get; set; } = "sub";

    public Dictionary<string, string> CustomMappings { get; set; } = [];

    public string[] AdditionalRoleClaimTypes { get; set; } = [];
}

public sealed class TokenValidationOptions
{
    public string? SymmetricSigningKey { get; set; }

    public bool ValidateIssuer { get; set; } = true;

    public bool ValidateAudience { get; set; } = true;

    public bool ValidateLifetime { get; set; } = true;

    public bool ValidateIssuerSigningKey { get; set; } = true;

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    public string[] ValidIssuers { get; set; } = [];

    public string[] ValidAudiences { get; set; } = [];

    public bool EnableTokenReplayProtection { get; set; } = true;

    public TimeSpan TokenReplayCacheDuration { get; set; } = TimeSpan.FromHours(24);

    // Replay protection fails open to per-node memory when Redis is unreachable; enable the fail-closed option when single-use must hold during a cache outage.
    public bool ReplayProtectionFailClosed { get; set; }
}

public sealed class OktaProviderOptions
{
    public bool Enabled { get; set; }

    public string? OrgUrl { get; set; }

    public string AuthorizationServerId { get; set; } = "default";

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc-okta";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc-okta";

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public bool RequestGroupsClaim { get; set; }

    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(OrgUrl) &&
        !string.IsNullOrEmpty(ClientId);

    public string GetAuthority() =>
        $"https://{OrgUrl}/oauth2/{AuthorizationServerId}";
}

public sealed class Auth0ProviderOptions
{
    public bool Enabled { get; set; }

    public string? Domain { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    // The Auth0 audience is distinct from the client id and is required when Auth0 should issue access tokens rather than ID tokens.
    public string? Audience { get; set; }

    public string? RoleClaimNamespace { get; set; }

    public string CallbackPath { get; set; } = "/signin-oidc-auth0";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc-auth0";

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(Domain) &&
        !string.IsNullOrEmpty(ClientId);

    // Auth0 issuer validation requires the trailing slash.
    public string GetAuthority() =>
        $"https://{Domain}/";
}

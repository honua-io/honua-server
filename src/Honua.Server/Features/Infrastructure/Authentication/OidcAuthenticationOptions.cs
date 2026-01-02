// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Authentication;

/// <summary>
/// Configuration options for OIDC authentication with multi-provider support.
/// </summary>
public sealed class OidcAuthenticationOptions
{
    /// <summary>
    /// Configuration section name in appsettings.json.
    /// </summary>
    public const string SectionName = "Oidc";

    /// <summary>
    /// Gets or sets whether OIDC authentication is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Azure AD provider configuration.
    /// </summary>
    public AzureAdProviderOptions? AzureAd { get; set; }

    /// <summary>
    /// Gets or sets the Google provider configuration.
    /// </summary>
    public GoogleProviderOptions? Google { get; set; }

    /// <summary>
    /// Gets or sets the generic OIDC provider configuration.
    /// </summary>
    public GenericOidcProviderOptions? Generic { get; set; }

    /// <summary>
    /// Gets or sets the claims mapping configuration.
    /// </summary>
    public ClaimsMappingOptions ClaimsMapping { get; set; } = new();

    /// <summary>
    /// Gets or sets the default role for authenticated users without explicit role claims.
    /// </summary>
    public string DefaultRole { get; set; } = "user";

    /// <summary>
    /// Gets or sets the roles that grant admin access.
    /// </summary>
    public string[] AdminRoles { get; set; } = ["admin", "administrator"];

    /// <summary>
    /// Gets or sets whether to require HTTPS for all OIDC operations.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Gets or sets the token validation options.
    /// </summary>
    public TokenValidationOptions TokenValidation { get; set; } = new();
}

/// <summary>
/// Azure AD provider configuration options.
/// </summary>
public sealed class AzureAdProviderOptions
{
    /// <summary>
    /// Gets or sets whether Azure AD authentication is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Azure AD tenant ID.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the application (client) ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret (for confidential clients).
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the Azure AD instance URL.
    /// </summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// Gets or sets the callback path for authentication response.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc-azuread";

    /// <summary>
    /// Gets or sets the signed-out callback path.
    /// </summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc-azuread";

    /// <summary>
    /// Gets or sets the scopes to request.
    /// </summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Gets whether the provider configuration is valid.
    /// </summary>
    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(TenantId) &&
        !string.IsNullOrEmpty(ClientId);
}

/// <summary>
/// Google provider configuration options.
/// </summary>
public sealed class GoogleProviderOptions
{
    /// <summary>
    /// Gets or sets whether Google authentication is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the Google OAuth client ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the Google OAuth client secret.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the callback path for authentication response.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc-google";

    /// <summary>
    /// Gets or sets the scopes to request.
    /// </summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Gets whether the provider configuration is valid.
    /// </summary>
    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(ClientId) &&
        !string.IsNullOrEmpty(ClientSecret);
}

/// <summary>
/// Generic OIDC provider configuration options.
/// </summary>
public sealed class GenericOidcProviderOptions
{
    /// <summary>
    /// Gets or sets whether the generic OIDC provider is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the display name for the provider.
    /// </summary>
    public string DisplayName { get; set; } = "External Identity Provider";

    /// <summary>
    /// Gets or sets the OIDC authority URL (issuer).
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Gets or sets the client ID.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the client secret.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the callback path for authentication response.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Gets or sets the signed-out callback path.
    /// </summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>
    /// Gets or sets the scopes to request.
    /// </summary>
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Gets or sets the response type (default: code for authorization code flow).
    /// </summary>
    public string ResponseType { get; set; } = "code";

    /// <summary>
    /// Gets or sets whether to use PKCE (Proof Key for Code Exchange).
    /// </summary>
    public bool UsePkce { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to save tokens in authentication properties.
    /// </summary>
    public bool SaveTokens { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to get claims from the userinfo endpoint.
    /// </summary>
    public bool GetClaimsFromUserInfoEndpoint { get; set; } = true;

    /// <summary>
    /// Gets whether the provider configuration is valid.
    /// </summary>
    public bool IsValid => Enabled &&
        !string.IsNullOrEmpty(Authority) &&
        !string.IsNullOrEmpty(ClientId);
}

/// <summary>
/// Claims mapping configuration for OIDC providers.
/// </summary>
public sealed class ClaimsMappingOptions
{
    /// <summary>
    /// Gets or sets the claim type to use for user name.
    /// </summary>
    public string NameClaimType { get; set; } = "name";

    /// <summary>
    /// Gets or sets the claim type to use for role.
    /// </summary>
    public string RoleClaimType { get; set; } = "roles";

    /// <summary>
    /// Gets or sets the claim type to use for email.
    /// </summary>
    public string EmailClaimType { get; set; } = "email";

    /// <summary>
    /// Gets or sets the claim type to use for user ID.
    /// </summary>
    public string UserIdClaimType { get; set; } = "sub";

    /// <summary>
    /// Gets or sets custom claim mappings (source claim -> target claim).
    /// </summary>
    public Dictionary<string, string> CustomMappings { get; set; } = [];
}

/// <summary>
/// Token validation options for OIDC/JWT authentication.
/// </summary>
public sealed class TokenValidationOptions
{
    /// <summary>
    /// Gets or sets whether to validate the token issuer.
    /// </summary>
    public bool ValidateIssuer { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to validate the token audience.
    /// </summary>
    public bool ValidateAudience { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to validate the token lifetime.
    /// </summary>
    public bool ValidateLifetime { get; set; } = true;

    /// <summary>
    /// Gets or sets whether to validate the signing key.
    /// </summary>
    public bool ValidateIssuerSigningKey { get; set; } = true;

    /// <summary>
    /// Gets or sets the clock skew tolerance for token expiration.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets additional valid issuers (for multi-tenant scenarios).
    /// </summary>
    public string[] ValidIssuers { get; set; } = [];

    /// <summary>
    /// Gets or sets additional valid audiences.
    /// </summary>
    public string[] ValidAudiences { get; set; } = [];
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Singleton cache of per-authority <see cref="ConfigurationManager{T}"/> instances used by
/// <see cref="OidcPortalCredentialVerifier"/>. Caching these singletons means that the
/// JWKS/metadata discovery result (including the signing keys) is retained across requests
/// instead of being fetched once per DI scope.
/// </summary>
internal sealed class OidcConfigurationManagerCache
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the shared <see cref="ConfigurationManager{T}"/> for the given
    /// <paramref name="metadataAddress"/>, creating it on first use.
    /// </summary>
    public ConfigurationManager<OpenIdConnectConfiguration> GetOrCreate(
        string metadataAddress,
        bool requireHttps)
    {
        return _managers.GetOrAdd(metadataAddress, addr =>
            new ConfigurationManager<OpenIdConnectConfiguration>(
                addr,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = requireHttps }));
    }
}

/// <summary>
/// OIDC-backed <see cref="IPortalCredentialVerifier"/> (#1370). A Portal named-user
/// login presents an OIDC token (id/access JWT issued by the operator's IdP) as the
/// <c>password</c> to <c>/sharing/rest/generateToken</c>; this verifier validates it
/// against the shared OIDC core (#348) and projects the resulting
/// <see cref="ClaimsPrincipal"/> onto a <see cref="PortalCredentialPrincipal"/>.
/// </summary>
/// <remarks>
/// <para>
/// Authentication therefore routes through the same identity pipeline as a direct
/// OIDC session: the token is validated with
/// <see cref="OidcTokenValidationParametersFactory"/> (the JwtBearer scheme's
/// issuer/audience/signing rules) and the claims are normalized by the shared
/// <see cref="OidcClaimsTransformation"/>. No Portal-local user store is introduced.
/// </para>
/// <para>
/// This avoids the deprecated Resource-Owner-Password-Credentials grant (which most
/// IdPs disable): the client obtains its token from the IdP via the normal OIDC
/// flow and relays it through <c>generateToken</c>. The issued portal token then
/// carries the same <c>PrincipalId</c>/<c>TenantId</c>/<c>Roles</c>, so
/// <c>AccessPolicyHelpers</c>/<c>IPermissionResolver</c> (#349) reach the identical
/// RBAC decision.
/// </para>
/// </remarks>
internal sealed class OidcPortalCredentialVerifier : IPortalCredentialVerifier
{
    private const string TenantIdClaimType = "tenant_id";
    private const string AzureTenantClaimType = "tid";

    private readonly OidcAuthenticationOptions _oidcOptions;
    private readonly IClaimsTransformation _claimsTransformation;
    private readonly ILogger<OidcPortalCredentialVerifier> _logger;
    private readonly OidcConfigurationManagerCache _configurationManagerCache;
    private readonly IOidcRuntimeProviderSource? _runtimeProviders;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcPortalCredentialVerifier"/> class.
    /// </summary>
    /// <param name="oidcOptions">The OIDC authentication options.</param>
    /// <param name="claimsTransformation">The shared OIDC claims transformation
    /// used to normalize provider claims into application claims.</param>
    /// <param name="configurationManagerCache">Singleton cache of per-authority
    /// <see cref="ConfigurationManager{T}"/> instances so JWKS metadata is shared
    /// across request scopes rather than fetched on every credential check.</param>
    /// <param name="logger">Diagnostics logger.</param>
    /// <param name="runtimeProviders">
    /// Admin-registered providers merged into the catalog for this process. Null keeps
    /// validation on the options-configured providers only.
    /// </param>
    public OidcPortalCredentialVerifier(
        IOptions<OidcAuthenticationOptions> oidcOptions,
        IClaimsTransformation claimsTransformation,
        OidcConfigurationManagerCache configurationManagerCache,
        ILogger<OidcPortalCredentialVerifier> logger,
        IOidcRuntimeProviderSource? runtimeProviders = null)
    {
        ArgumentNullException.ThrowIfNull(oidcOptions);
        ArgumentNullException.ThrowIfNull(configurationManagerCache);
        _oidcOptions = oidcOptions.Value ?? throw new ArgumentNullException(nameof(oidcOptions));
        _claimsTransformation = claimsTransformation ?? throw new ArgumentNullException(nameof(claimsTransformation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configurationManagerCache = configurationManagerCache;
        _runtimeProviders = runtimeProviders;
    }

    /// <inheritdoc />
    public Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
        => VerifyCoreAsync(username, password, providerKey: null, cancellationToken);

    /// <summary>
    /// Validates an IdP token against the provider that started the broker flow.
    /// A token signed by a different configured authority is rejected.
    /// </summary>
    public Task<PortalCredentialPrincipal?> VerifyForProviderAsync(
        string providerKey,
        string token,
        CancellationToken cancellationToken)
        => VerifyCoreAsync(username: string.Empty, token, providerKey, cancellationToken);

    private async Task<PortalCredentialPrincipal?> VerifyCoreAsync(
        string username,
        string password,
        string? providerKey,
        CancellationToken cancellationToken)
    {
        if (!_oidcOptions.Enabled || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var parameters = await ResolveParametersAsync(providerKey, cancellationToken).ConfigureAwait(false);
        if (parameters is null)
        {
            return null;
        }

        var result = await _handler.ValidateTokenAsync(password, parameters).ConfigureAwait(false);
        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
#pragma warning disable CA1873 // Reason string only evaluated inside the IsEnabled gate above.
                OidcAuthenticationLog.PortalCredentialRejected(_logger, result.Exception?.GetType().Name ?? "invalid token");
#pragma warning restore CA1873
            }

            return null;
        }

        // Normalize provider claims through the shared OIDC transformation so the
        // projected roles/name match a direct OIDC session exactly.
        var principal = new ClaimsPrincipal(result.ClaimsIdentity);
        principal = await _claimsTransformation.TransformAsync(principal).ConfigureAwait(false);
        if (principal.Identity is not { IsAuthenticated: true })
        {
            // Managed membership turns a deprovisioned or unreachable account into an
            // unauthenticated principal. That result must not still mint a portal token.
            OidcAuthenticationLog.PortalCredentialRejected(_logger, "principal is not authenticated");
            return null;
        }

        var principalId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? principal.FindFirstValue("oid")
            ?? (string.IsNullOrWhiteSpace(username) ? null : username);

        if (string.IsNullOrWhiteSpace(principalId))
        {
            OidcAuthenticationLog.PortalCredentialRejected(_logger, "no subject claim");
            return null;
        }

        var displayName = principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? principal.FindFirstValue("preferred_username");

        var tenantClaim = principal.FindFirst(TenantIdClaimType)
            ?? principal.FindFirst(AzureTenantClaimType);
        var tenantId = tenantClaim?.Value;

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(static c => c.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Carry the transformation's provenance marker so the issued token knows whether its
        // roles depend on an entitlement that can expire (honua-server#2997 review).
        var rolesRequireClaimsMapping = principal.HasClaim(
            static claim => claim.Type == OidcClaimsTransformation.RolesFromClaimsMappingClaimType);
        var rolesWithoutClaimsMapping = rolesRequireClaimsMapping
            ? principal.FindAll(OidcClaimsTransformation.RolesWithoutClaimsMappingClaimType)
                .Select(static claim => claim.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : null;

        // Same provenance question for the tenant: a CustomMappings entry can synthesize it,
        // and a mapping-derived tenant that outlives the entitlement authorizes another
        // tenant's data (honua-server#2997 review).
        var tenantMappingMarker = principal.FindFirst(
            OidcClaimsTransformation.TenantFromClaimsMappingClaimType);
        var markerNamesKnownClaimType = tenantMappingMarker?.Value is TenantIdClaimType or AzureTenantClaimType;
        var tenantRequiresClaimsMapping = tenantMappingMarker is not null &&
            (!markerNamesKnownClaimType || tenantClaim is null ||
                string.Equals(tenantMappingMarker.Value, tenantClaim.Type, StringComparison.Ordinal));

        // Bind the issued portal token to the bridged token's own expiry so it can never
        // outlive the credential it was minted from (SEC-9). The issuer/subject pair records
        // which identity it came from; no token material is kept.
        var bridged = result.SecurityToken as JsonWebToken;
        var bridgedExpiry = bridged?.ValidTo is { } validTo && validTo != DateTime.MinValue
            ? new DateTimeOffset(DateTime.SpecifyKind(validTo, DateTimeKind.Utc))
            : (DateTimeOffset?)null;

        // OIDC lifetime validation permits clock skew, but a derived credential must
        // have a future expiry. Refuse an already-expired source instead of issuing
        // an unusable portal token or passing a past expiration to distributed cache.
        if (bridgedExpiry is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
        {
            OidcAuthenticationLog.PortalCredentialRejected(_logger, "source token expired");
            return null;
        }

        return new PortalCredentialPrincipal(
            principalId, displayName, tenantId, roles, rolesRequireClaimsMapping,
            tenantRequiresClaimsMapping, rolesWithoutClaimsMapping,
            Source: new PortalCredentialSource(
                PortalCredentialSourceKind.FederatedToken,
                Reference: $"{bridged?.Issuer}|{principalId}",
                Version: null,
                ExpiresAt: bridgedExpiry));
    }

    private async Task<TokenValidationParameters?> ResolveParametersAsync(
        string? providerKey,
        CancellationToken cancellationToken)
    {
        var built = OidcTokenValidationParametersFactory.Build(_oidcOptions, _runtimeProviders);
        var parameters = built.Parameters;
        var authorities = built.Authorities;

        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            if (!OidcProviderCatalog.TryResolveProvider(
                    _oidcOptions,
                    providerKey,
                    out var provider,
                    _runtimeProviders)
                || string.IsNullOrWhiteSpace(provider.Authority))
            {
                OidcAuthenticationLog.PortalCredentialRejected(_logger, "provider was not found");
                return null;
            }

            parameters = OidcTokenValidationParametersFactory.NarrowToProvider(parameters, provider, _oidcOptions);
            authorities = [provider.Authority.Trim().TrimEnd('/')];
        }

        if (parameters.IssuerSigningKey is not null)
        {
            // Static symmetric signing key: no metadata discovery. Issuer and audience
            // pairing above still apply, including a selected provider.
            return parameters;
        }

        if (authorities.Count == 0)
        {
            return null;
        }

        var keysByIssuer = new Dictionary<string, List<SecurityKey>>(StringComparer.OrdinalIgnoreCase);
        Exception? firstFailure = null;
        foreach (var authority in authorities)
        {
            var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            try
            {
                var configuration = await _configurationManagerCache
                    .GetOrCreate(metadataAddress, _oidcOptions.RequireHttps)
                    .GetConfigurationAsync(cancellationToken)
                    .ConfigureAwait(false);
                var signingKeys = configuration.SigningKeys?.ToList() ?? [];
                if (signingKeys.Count == 0)
                {
                    continue;
                }

                // Bind the keys to the discovery issuer and to the authority URL. A union
                // assigned to IssuerSigningKeys would let issuer A's token validate with
                // issuer B's key, and a null resolver result falls back to that union.
                AddIssuerKeys(keysByIssuer, configuration.Issuer, signingKeys);
                AddIssuerKeys(keysByIssuer, authority, signingKeys);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable authority must not hide the keys of the others.
                firstFailure ??= ex;
            }
        }

        if (keysByIssuer.Count == 0)
        {
            OidcAuthenticationLog.PortalCredentialMetadataUnavailable(
                _logger,
                firstFailure?.Message ?? "no signing keys");
            return null;
        }

        var merged = parameters.Clone();
        merged.IssuerSigningKeys = null;
        merged.TryAllIssuerSigningKeys = false;
        merged.IssuerSigningKeyResolver = (_, securityToken, _, _) =>
        {
            var issuer = securityToken?.Issuer;
            if (string.IsNullOrWhiteSpace(issuer))
            {
                return [];
            }

            if (keysByIssuer.TryGetValue(issuer.Trim(), out var matched))
            {
                return matched;
            }

            var trimmed = issuer.Trim().TrimEnd('/');
            return keysByIssuer.TryGetValue(trimmed, out matched) ? matched : [];
        };
        return merged;
    }

    private static void AddIssuerKeys(
        Dictionary<string, List<SecurityKey>> keysByIssuer,
        string? issuer,
        List<SecurityKey> signingKeys)
    {
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return;
        }

        foreach (var candidate in new[] { issuer.Trim(), issuer.Trim().TrimEnd('/') })
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            if (!keysByIssuer.TryGetValue(candidate, out var list))
            {
                list = [];
                keysByIssuer[candidate] = list;
            }

            foreach (var key in signingKeys)
            {
                if (!list.Contains(key))
                {
                    list.Add(key);
                }
            }
        }
    }
}

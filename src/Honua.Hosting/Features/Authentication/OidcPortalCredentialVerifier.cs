// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
    private readonly TokenValidationParameters _baseParameters;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configurationManager;
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcPortalCredentialVerifier"/> class.
    /// </summary>
    /// <param name="oidcOptions">The OIDC authentication options.</param>
    /// <param name="claimsTransformation">The shared OIDC claims transformation
    /// used to normalize provider claims into application claims.</param>
    /// <param name="logger">Diagnostics logger.</param>
    public OidcPortalCredentialVerifier(
        IOptions<OidcAuthenticationOptions> oidcOptions,
        IClaimsTransformation claimsTransformation,
        ILogger<OidcPortalCredentialVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(oidcOptions);
        _oidcOptions = oidcOptions.Value ?? throw new ArgumentNullException(nameof(oidcOptions));
        _claimsTransformation = claimsTransformation ?? throw new ArgumentNullException(nameof(claimsTransformation));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var (parameters, authority) = OidcTokenValidationParametersFactory.Build(_oidcOptions);
        _baseParameters = parameters;

        if (!string.IsNullOrWhiteSpace(authority))
        {
            var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            _configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = _oidcOptions.RequireHttps });
        }
    }

    /// <inheritdoc />
    public async Task<PortalCredentialPrincipal?> VerifyAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        if (!_oidcOptions.Enabled || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var parameters = await ResolveParametersAsync(cancellationToken).ConfigureAwait(false);
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

        var tenantId = principal.FindFirstValue(TenantIdClaimType)
            ?? principal.FindFirstValue(AzureTenantClaimType);

        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(static c => c.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new PortalCredentialPrincipal(principalId, displayName, tenantId, roles);
    }

    private async Task<TokenValidationParameters?> ResolveParametersAsync(CancellationToken cancellationToken)
    {
        if (_configurationManager is null)
        {
            // Static symmetric signing key path: the base parameters already carry
            // the issuer signing key and need no metadata discovery.
            return _baseParameters.IssuerSigningKey is null ? null : _baseParameters;
        }

        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OidcAuthenticationLog.PortalCredentialMetadataUnavailable(_logger, ex.Message);
            return null;
        }

        var parameters = _baseParameters.Clone();
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        if (parameters.ValidIssuers is null || !parameters.ValidIssuers.Any())
        {
            parameters.ValidIssuer = configuration.Issuer;
        }

        return parameters;
    }
}

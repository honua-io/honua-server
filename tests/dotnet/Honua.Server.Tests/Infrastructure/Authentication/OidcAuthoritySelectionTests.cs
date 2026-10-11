// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// The portal credential verifier uses catalog order and the selected provider's
/// issuer and audience. A token from another configured authority is not accepted
/// for a broker flow that started at a different provider.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OidcAuthoritySelectionTests
{
    private const string SigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string GoogleAudience = "google-client";
    private const string OktaIssuer = "https://dev-12345.okta.com/oauth2/default";
    private const string OktaAudience = "okta-client";

    [UnitTest]
    public void Build_UsesCatalogAuthorityOrder_IncludingRuntimeProviders()
    {
        var registry = new OidcRuntimeProviderRegistry();
        try
        {
            registry.Replace(
            [
                new OidcRuntimeProvider(
                    "runtime-1",
                    "Generic",
                    "Runtime",
                    "https://runtime.example.com",
                    "runtime-client",
                    Enabled: true),
            ]);
            var built = OidcTokenValidationParametersFactory.Build(AllProviders(), registry);

            Assert.Equal(
                [
                    "https://login.microsoftonline.com/contoso/v2.0",
                    GoogleIssuer,
                    OktaIssuer,
                    "https://tenant.us.auth0.com",
                    "https://idp.example.com/generic",
                    "https://runtime.example.com",
                ],
                built.Authorities);
        }
        finally
        {
            OidcProviderCatalog.Attach(null);
        }
    }

    [UnitTest]
    public async Task VerifyForProvider_RejectsATokenFromAnotherConfiguredAuthority()
    {
        var verifier = CreateVerifier();
        var oktaToken = CreateToken(OktaIssuer, OktaAudience);
        var googleToken = CreateToken(GoogleIssuer, GoogleAudience);
        var crossed = CreateToken(GoogleIssuer, OktaAudience);

        var selectedOkta = await verifier.VerifyForProviderAsync("okta", oktaToken, CancellationToken.None);
        var anyProvider = await verifier.VerifyAsync("user", oktaToken, CancellationToken.None);
        var substituted = await verifier.VerifyForProviderAsync("google", oktaToken, CancellationToken.None);
        var google = await verifier.VerifyForProviderAsync("google", googleToken, CancellationToken.None);
        var wrongAudience = await verifier.VerifyAsync("user", crossed, CancellationToken.None);

        Assert.NotNull(selectedOkta);
        Assert.NotNull(anyProvider);
        Assert.NotNull(google);
        Assert.Null(substituted);
        Assert.Null(wrongAudience);
    }

    private static OidcPortalCredentialVerifier CreateVerifier()
    {
        var options = AllProviders();
        options.TokenValidation.SymmetricSigningKey = SigningKey;
        var wrapped = Options.Create(options);
        var transformation = new OidcClaimsTransformation(
            wrapped,
            NullLogger<OidcClaimsTransformation>.Instance,
            new ServiceCollection().BuildServiceProvider());
        return new OidcPortalCredentialVerifier(
            wrapped,
            transformation,
            new OidcConfigurationManagerCache(),
            NullLogger<OidcPortalCredentialVerifier>.Instance);
    }

    private static OidcAuthenticationOptions AllProviders() => new()
    {
        Enabled = true,
        RequireHttps = false,
        AzureAd = new AzureAdProviderOptions
        {
            Enabled = true,
            TenantId = "contoso",
            ClientId = "azure-client",
            ClientSecret = "secret",
        },
        Google = new GoogleProviderOptions
        {
            Enabled = true,
            ClientId = GoogleAudience,
            ClientSecret = "secret",
        },
        Okta = new OktaProviderOptions
        {
            Enabled = true,
            OrgUrl = "dev-12345.okta.com",
            ClientId = OktaAudience,
            ClientSecret = "secret",
        },
        Auth0 = new Auth0ProviderOptions
        {
            Enabled = true,
            Domain = "tenant.us.auth0.com",
            ClientId = "auth0-client",
            ClientSecret = "secret",
            Audience = "https://api.example.com",
        },
        Generic = new GenericOidcProviderOptions
        {
            Enabled = true,
            Authority = "https://idp.example.com/generic",
            ClientId = "generic-client",
        },
    };

    private static string CreateToken(string issuer, string audience)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("sub", "user-1"), new Claim("name", "User One")],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler { MapInboundClaims = false }.WriteToken(token);
    }
}

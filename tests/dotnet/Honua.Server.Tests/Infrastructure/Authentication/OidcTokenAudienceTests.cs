// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OidcTokenAudienceTests
{
    // An explicit empty source keeps these tests off the process-wide runtime source that any test host attaches.
    private static readonly IOidcRuntimeProviderSource NoRuntimeProviders = new EmptyRuntimeProviderSource();

    [UnitTheory]
    [InlineData(true, "https://issuer.example", "configured-client", "access", null, true)]
    [InlineData(false, "https://issuer.example", "configured-client", "access", null, false)]
    [InlineData(true, "https://other.example", "configured-client", "access", null, false)]
    [InlineData(true, "https://issuer.example", "other-client", "access", null, false)]
    [InlineData(true, "https://issuer.example", null, "access", null, false)]
    [InlineData(true, "https://issuer.example", "configured-client", "id", null, false)]
    [InlineData(true, "https://issuer.example", "configured-client", null, null, false)]
    [InlineData(true, "https://issuer.example", "configured-client", "access", "wrong-audience", false)]
    [InlineData(false, "https://issuer.example", null, "id", "configured-client", true)]
    public void ClientIdFallback_IsOptInAndBoundToAccessTokenIssuerAndClient(
        bool enabled, string issuer, string? clientId, string? tokenUse, string? audience, bool expected)
    {
        var options = CreateOptions(enabled);
        var parameters = OidcTokenValidationParametersFactory.Build(options, NoRuntimeProviders).Parameters;
        var claims = new List<Claim>();
        if (clientId is not null)
        {
            claims.Add(new Claim("client_id", clientId));
        }
        if (tokenUse is not null)
        {
            claims.Add(new Claim("token_use", tokenUse));
        }
        var token = new JwtSecurityToken(issuer: issuer, audience: audience, claims: claims);
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        SecurityToken[] tokens = [token, new JsonWebToken(encoded)];

        foreach (var parsed in tokens)
        {
            Assert.Equal(expected, parameters.AudienceValidator!(token.Audiences, parsed, parameters));
        }
    }

    [UnitTheory]
    [InlineData("client_id", "configured-client")]
    [InlineData("token_use", "access")]
    [InlineData("aud", "")]
    public void ClientIdFallback_RejectsDuplicateBindingClaimsAndPresentAudience(string claimType, string value)
    {
        var parameters = OidcTokenValidationParametersFactory.Build(CreateOptions(true), NoRuntimeProviders).Parameters;
        var token = new JwtSecurityToken(issuer: "https://issuer.example", claims:
        [
            new Claim("client_id", "configured-client"),
            new Claim("token_use", "access"),
            new Claim(claimType, value)
        ]);
        var encoded = new JwtSecurityTokenHandler().WriteToken(token);
        SecurityToken[] tokens = [token, new JsonWebToken(encoded)];

        foreach (var parsed in tokens)
        {
            Assert.False(parameters.AudienceValidator!(token.Audiences, parsed, parameters));
        }
    }

    [UnitTheory]
    [InlineData("[]")]
    [InlineData("null")]
    public void ClientIdFallback_RejectsEmptyAudiencePayload(string audienceJson)
    {
        var parameters = OidcTokenValidationParametersFactory.Build(CreateOptions(true), NoRuntimeProviders).Parameters;
        var payload = "{\"iss\":\"https://issuer.example\",\"client_id\":\"configured-client\",\"token_use\":\"access\",\"aud\":" + audienceJson + "}";
        var encoded = Base64UrlEncoder.Encode("{\"alg\":\"none\"}") + "." + Base64UrlEncoder.Encode(payload) + ".";
        SecurityToken[] tokens = [new JwtSecurityToken(encoded), new JsonWebToken(encoded)];

        foreach (var token in tokens)
        {
            Assert.False(parameters.AudienceValidator!([], token, parameters));
        }
    }

    [UnitTest]
    public void ClientIdFallback_DoesNotRelaxBrokerIdTokenAudience()
    {
        var options = CreateOptions(true);
        var parameters = OidcTokenValidationParametersFactory.Build(options, NoRuntimeProviders).Parameters;
        var provider = Assert.Single(OidcProviderCatalog.GetProviders(options, NoRuntimeProviders));
        var narrowed = OidcTokenValidationParametersFactory.NarrowToProvider(parameters, provider, options);
        var token = new JwtSecurityToken(issuer: "https://issuer.example",
            claims: [new Claim("client_id", "configured-client"), new Claim("token_use", "access")]);

        Assert.False(narrowed.AudienceValidator!(token.Audiences, token, narrowed));
    }

    private static OidcAuthenticationOptions CreateOptions(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Generic:Enabled"] = "true",
            ["Generic:Authority"] = "https://issuer.example",
            ["Generic:ClientId"] = "configured-client",
            ["Generic:AcceptClientIdAsAudience"] = enabled.ToString()
        }).Build();
        var options = new OidcAuthenticationOptions();
        configuration.Bind(options);
        return options;
    }

    private sealed class EmptyRuntimeProviderSource : IOidcRuntimeProviderSource
    {
        public IReadOnlyList<OidcRuntimeProvider> Current { get; } = [];
    }
}

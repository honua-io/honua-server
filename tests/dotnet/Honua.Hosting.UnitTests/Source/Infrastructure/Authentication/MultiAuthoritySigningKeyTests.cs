// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// A token's signature is accepted only with keys from the authority whose issuer
/// the token names, even though the JwtBearer handler copies every provider's keys
/// onto <see cref="TokenValidationParameters.IssuerSigningKeys"/> (honua-server#6165).
/// </summary>
public sealed class MultiAuthoritySigningKeyTests
{
    private const string IssuerA = "https://idp-a.example/v2.0";
    private const string IssuerB = "https://idp-b.example";
    private const string AudienceA = "a-client";

    [UnitTest]
    public async Task TokenSignedByAnotherProvidersKey_IsRejected_SameIssuerKey_IsAccepted()
    {
        using var rsaA = RSA.Create(2048);
        using var rsaB = RSA.Create(2048);
        var keyA = new RsaSecurityKey(rsaA) { KeyId = "key-a" };
        var keyB = new RsaSecurityKey(rsaB) { KeyId = "key-b" };
        var manager = new MultiAuthorityConfigurationManager(
        [
            (IssuerA, Discovery(IssuerA, keyA)),
            (IssuerB, Discovery(IssuerB, keyB)),
        ]);

        var configuration = await manager.GetConfigurationAsync(CancellationToken.None);
        configuration.SigningKeys.Should().HaveCount(2, "the handler still receives the merged document");

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = [IssuerA, IssuerB],
            ValidateAudience = true,
            ValidAudience = AudienceA,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };
        manager.BindIssuerSigningKeys(parameters);
        // JwtBearerHandler concatenates configuration.SigningKeys after cloning parameters.
        parameters.IssuerSigningKeys = configuration.SigningKeys;

        var handler = new JsonWebTokenHandler();
        var crossSigned = handler.CreateToken(Token(IssuerA, AudienceA, keyB));
        var sameIssuer = handler.CreateToken(Token(IssuerA, AudienceA, keyA));

        var rejected = await handler.ValidateTokenAsync(crossSigned, parameters);
        var accepted = await handler.ValidateTokenAsync(sameIssuer, parameters);

        rejected.IsValid.Should().BeFalse(
            "issuer A's token must not validate with issuer B's key. {0}", rejected.Exception?.Message);
        accepted.IsValid.Should().BeTrue(
            "the same token signed by A's own key must be accepted. {0}", accepted.Exception?.Message);
    }

    private static OpenIdConnectConfiguration Discovery(string issuer, SecurityKey key)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(key);
        return configuration;
    }

    private static SecurityTokenDescriptor Token(string issuer, string audience, SecurityKey key)
        => new()
        {
            Issuer = issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
        };
}

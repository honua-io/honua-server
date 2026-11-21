// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Honua.Hosting.UnitTests.Authentication;

[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class OidcPortalBridgeReplayTests
{
    private const string SigningKey = "ThisIsATestSigningKeyThatIsLongEnoughForHS256Algorithm!";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-portal-client";

    [UnitTest]
    public void ReplayProtection_FailsClosedByDefault()
        => Assert.True(new TokenValidationOptions().ReplayProtectionFailClosed);

    [UnitTest]
    public async Task Exchange_SameTokenTwice_IsAccepted()
    {
        var verifier = CreateVerifier(new InMemoryTokenReplayStore(), new TokenValidationOptions());
        var token = CreateToken();

        Assert.NotNull(await verifier.VerifyAsync("ada", token, CancellationToken.None));
        Assert.NotNull(await verifier.VerifyAsync("ada", token, CancellationToken.None));
    }

    [UnitTest]
    public async Task Exchange_TokenBoundToAnotherSurface_IsRefused()
    {
        var replays = Substitute.For<ITokenReplayStore>();
        replays.TryRegisterAsync(default, default)
            .ReturnsForAnyArgs(new TokenReplayRegistrationOutcome(false, "surface:mcp:validated-token"));
        var verifier = CreateVerifier(replays, new TokenValidationOptions());

        Assert.Null(await verifier.VerifyAsync("ada", CreateToken(), CancellationToken.None));
    }

    [UnitTest]
    public async Task Exchange_ReplayStoreUnavailable_IsRefusedByDefault()
    {
        var verifier = CreateVerifier(UnavailableStore(), new TokenValidationOptions());

        Assert.Null(await verifier.VerifyAsync("ada", CreateToken(), CancellationToken.None));
    }

    [UnitTest]
    public async Task Exchange_ReplayStoreUnavailable_IsAcceptedWhenFailOpenIsConfigured()
    {
        var verifier = CreateVerifier(UnavailableStore(), new TokenValidationOptions { ReplayProtectionFailClosed = false });

        Assert.NotNull(await verifier.VerifyAsync("ada", CreateToken(), CancellationToken.None));
    }

    [UnitTest]
    public async Task Exchange_TokenOutlivingBearerRetention_IsAccepted()
    {
        var validation = new TokenValidationOptions { TokenReplayCacheDuration = TimeSpan.FromMinutes(10) };
        var verifier = CreateVerifier(new InMemoryTokenReplayStore(), validation);

        Assert.NotNull(await verifier.VerifyAsync("ada", CreateToken(TimeSpan.FromHours(1)), CancellationToken.None));
    }

    private static ITokenReplayStore UnavailableStore()
    {
        var replays = Substitute.For<ITokenReplayStore>();
        replays.TryRegisterAsync(default, default)
            .ReturnsForAnyArgs(Task.FromException<TokenReplayRegistrationOutcome>(new InvalidOperationException("database unavailable")));
        return replays;
    }

    private static OidcPortalCredentialVerifier CreateVerifier(ITokenReplayStore replays, TokenValidationOptions validation)
    {
        validation.SymmetricSigningKey = SigningKey;
        validation.ValidIssuers = [Issuer];
        validation.ValidAudiences = [Audience];
        var options = Options.Create(new OidcAuthenticationOptions
        {
            Enabled = true,
            RequireHttps = false,
            Generic = new GenericOidcProviderOptions { Enabled = true, Authority = Issuer, ClientId = Audience },
            TokenValidation = validation,
        });
        var transformation = new OidcClaimsTransformation(
            options,
            NullLogger<OidcClaimsTransformation>.Instance,
            new ServiceCollection().BuildServiceProvider());
        return new OidcPortalCredentialVerifier(
            options,
            transformation,
            new OidcConfigurationManagerCache(),
            NullLogger<OidcPortalCredentialVerifier>.Instance,
            replays: replays);
    }

    private static string CreateToken(TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim("sub", "user-123"), new Claim("jti", Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler { MapInboundClaims = false }.WriteToken(token);
    }
}

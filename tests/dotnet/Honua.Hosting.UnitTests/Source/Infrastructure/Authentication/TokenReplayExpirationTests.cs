// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static Honua.Infrastructure.Authentication.OidcAuthenticationExtensions;

namespace Honua.Hosting.UnitTests.Authentication;

[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class TokenReplayExpirationTests
{
    private static readonly DateTime Now = DateTime.UnixEpoch.AddDays(22000);

    [UnitTheory]
    [InlineData(60, 120, 5, true)]
    [InlineData(60, 10, 5, false)]
    [InlineData(115, 120, 5, true)]
    [InlineData(116, 120, 5, false)]
    [InlineData(-4, 120, 5, true)]
    [InlineData(-5, 120, 5, false)]
    [InlineData(-6, 120, 5, false)]
    public void Expiration_RemainingValidityAndSkew_RejectsExpiredOrOverLimitTokens(int lifetimeMinutes, int maximumMinutes, int skewMinutes, bool admitted)
    {
        var token = new JwtSecurityToken(expires: Now.AddMinutes(lifetimeMinutes));
        var options = new TokenValidationOptions
        {
            ClockSkew = TimeSpan.FromMinutes(skewMinutes),
            TokenReplayCacheDuration = TimeSpan.FromMinutes(maximumMinutes)
        };
        var expiration = GetReplayCacheExpiration(token, options, Now);
        Assert.Equal(admitted ? token.ValidTo.AddMinutes(skewMinutes) : (DateTime?)null, expiration);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiration_BothValidatedTokenRepresentations_KeepsAbsoluteExpiryOnLaterRequests(bool useJsonWebToken)
    {
        var jwt = new JwtSecurityToken(expires: Now.AddHours(1));
        SecurityToken token = useJsonWebToken ? new JsonWebToken(new JwtSecurityTokenHandler().WriteToken(jwt)) : jwt;
        var options = new TokenValidationOptions();
        var expiration = GetReplayCacheExpiration(token, options, Now);
        Assert.Equal(Now.AddMinutes(65), expiration);
        Assert.Equal(expiration, GetReplayCacheExpiration(token, options, Now.AddMinutes(15)));
        Assert.Equal(expiration, GetReplayCacheExpiration(token, options, Now.AddMinutes(64)));
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(24)]
    public void Expiration_DefaultOrZeroMaximum_IsBounded(int configuredHours)
    {
        var token = new JwtSecurityToken(expires: Now.AddHours(25));
        var options = new TokenValidationOptions { TokenReplayCacheDuration = TimeSpan.FromHours(configuredHours) };
        Assert.Null(GetReplayCacheExpiration(token, options, Now));
    }

    [UnitTheory]
    [InlineData(0)]
    [InlineData(24)]
    public void Expiration_DefaultOrZeroMaximum_AdmitsTokenAtRetentionBoundary(int configuredHours)
    {
        var token = new JwtSecurityToken(expires: Now.AddHours(23).AddMinutes(55));
        var options = new TokenValidationOptions
        {
            ClockSkew = TimeSpan.FromMinutes(5),
            TokenReplayCacheDuration = TimeSpan.FromHours(configuredHours)
        };
        Assert.Equal(token.ValidTo.Add(options.ClockSkew), GetReplayCacheExpiration(token, options, Now));
    }

    [UnitTest]
    public void Expiration_TokenWithoutExpiry_IsRefused()
    {
        Assert.Null(GetReplayCacheExpiration(new JwtSecurityToken(), new TokenValidationOptions(), Now));
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Identity;

[Protocol(TestProtocols.Admin)]
public sealed class SamlRequestCorrelationTests
{
    private const string RequestId = "_0123456789abcdef0123456789abcdef";

    private readonly EphemeralDataProtectionProvider _protection = new();

    private SamlRequestCorrelation CreateCorrelation(string? signOnUrl = SamlSignInTestFlow.IdpSignOnUrl)
    {
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            IdpSingleSignOnServiceUrl = signOnUrl,
        };
        var replay = new SamlAssertionReplayCache(new InMemoryTokenReplayStore());
        return new SamlRequestCorrelation(_protection, replay, Options.Create(options));
    }

    // Issues the cookie on one context and returns a second context that sends it back.
    private static DefaultHttpContext RoundTripCookie(SamlRequestCorrelation correlation)
    {
        var issuing = new DefaultHttpContext();
        correlation.Issue(issuing, RequestId);
        var setCookie = issuing.Response.Headers.SetCookie.ToString();
        var returning = new DefaultHttpContext();
        returning.Request.Headers.Cookie = setCookie.Split(';')[0];
        return returning;
    }

    [UnitTest]
    public void Verify_MatchingCookie_AcceptsOnceThenRefuses()
    {
        var correlation = CreateCorrelation();
        var context = RoundTripCookie(correlation);

        Assert.Null(correlation.Verify(context, RequestId));
        Assert.Equal(SamlRequestCorrelation.NotCorrelated, correlation.Verify(context, RequestId));
    }

    [UnitTest]
    public void Verify_DifferentRequestId_IsRefused()
    {
        var correlation = CreateCorrelation();
        var context = RoundTripCookie(correlation);

        Assert.Equal(SamlRequestCorrelation.NotCorrelated, correlation.Verify(context, "_ffffffffffffffffffffffffffffffff"));
    }

    [UnitTest]
    public void Verify_MissingOrTamperedCookie_IsRefused()
    {
        var correlation = CreateCorrelation();
        var tampered = new DefaultHttpContext();
        tampered.Request.Headers.Cookie = SamlRequestCorrelation.CookieName + "=" + RequestId;

        Assert.Equal(SamlRequestCorrelation.NotCorrelated, correlation.Verify(new DefaultHttpContext(), RequestId));
        Assert.Equal(SamlRequestCorrelation.NotCorrelated, correlation.Verify(tampered, RequestId));
    }

    [UnitTest]
    public void Verify_CookieFromAnotherKeyRing_IsRefused()
    {
        var context = RoundTripCookie(CreateCorrelation());
        var otherKeys = new SamlRequestCorrelation(
            new EphemeralDataProtectionProvider(),
            new SamlAssertionReplayCache(new InMemoryTokenReplayStore()),
            Options.Create(new SamlAuthenticationOptions()));

        Assert.Equal(SamlRequestCorrelation.NotCorrelated, otherKeys.Verify(context, RequestId));
    }

    [UnitTest]
    public void Verify_Unsolicited_FollowsTheDefaultPolicy()
    {
        Assert.Equal(SamlRequestCorrelation.UnsolicitedRejected, CreateCorrelation().Verify(new DefaultHttpContext(), null));
        Assert.Null(CreateCorrelation(signOnUrl: null).Verify(new DefaultHttpContext(), null));
    }

    [UnitTest]
    public void Clear_ExpiresTheCorrelationCookie()
    {
        var context = new DefaultHttpContext();

        SamlRequestCorrelation.Clear(context);

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.StartsWith(SamlRequestCorrelation.CookieName + "=;", setCookie, StringComparison.Ordinal);
        Assert.Contains("1970", setCookie, StringComparison.Ordinal);
    }

    private static bool SignOnUrlIsValid(string? signOnUrl)
    {
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            EntityId = SamlTestAssertions.Audience,
            IdpEntityId = SamlTestAssertions.Issuer,
            AssertionConsumerServiceUrl = SamlTestAssertions.AcsUrl,
            IdpSingleSignOnServiceUrl = signOnUrl,
        };
        return new SamlAuthenticationOptionsValidator().Validate(null, options).Succeeded;
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("https://idp.example.com/sso")]
    public void OptionsValidator_AcceptsAnUnsetOrHttpsSignOnUrl(string? signOnUrl)
        => Assert.True(SignOnUrlIsValid(signOnUrl));

    [UnitTheory]
    [InlineData("http://idp.example.com/sso")]
    [InlineData("/sso")]
    [InlineData("not a url")]
    public void OptionsValidator_RefusesANonHttpsSignOnUrl(string signOnUrl)
        => Assert.False(SignOnUrlIsValid(signOnUrl));

    [UnitTest]
    public void OptionsValidator_RefusesOptingOutOfUnsolicitedResponsesWithoutASignOnUrl()
    {
        var options = new SamlAuthenticationOptions
        {
            Enabled = true,
            EntityId = SamlTestAssertions.Audience,
            IdpEntityId = SamlTestAssertions.Issuer,
            AssertionConsumerServiceUrl = SamlTestAssertions.AcsUrl,
            AllowUnsolicitedResponses = false,
        };

        Assert.False(new SamlAuthenticationOptionsValidator().Validate(null, options).Succeeded);
        options.IdpSingleSignOnServiceUrl = "https://idp.example.com/sso";
        Assert.True(new SamlAuthenticationOptionsValidator().Validate(null, options).Succeeded);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Identity;

// A deployment with SP-initiated sign-in configured: responses must answer a request this browser started.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class SamlSolicitedSignInTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = SamlTestAssertions.CreateSigningCertificate();
    private readonly WebAppFixture _fixture;

    public SamlSolicitedSignInTests()
    {
        _fixture = SamlSignInTestFlow.CreateFixture(
            _certificate,
            new Dictionary<string, string> { ["Saml:IdpSingleSignOnServiceUrl"] = SamlSignInTestFlow.IdpSignOnUrl });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync()
    {
        _certificate.Dispose();
        return _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /saml/login")]
    public async Task Login_RedirectsToIdpWithRequestAndCorrelationCookie()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);

        Assert.StartsWith(SamlSignInTestFlow.IdpSignOnUrl + "?", signIn.Location.ToString(), StringComparison.Ordinal);
        Assert.Matches("^_[0-9a-f]{32}$", signIn.RequestId);
        var attributes = signIn.SetCookieHeader.ToLowerInvariant();
        Assert.Contains("secure", attributes, StringComparison.Ordinal);
        Assert.Contains("httponly", attributes, StringComparison.Ordinal);
        Assert.Contains("samesite=none", attributes, StringComparison.Ordinal);
        Assert.Contains("path=/saml", attributes, StringComparison.Ordinal);
        Assert.Contains("max-age=600", attributes, StringComparison.Ordinal);
        Assert.DoesNotContain(signIn.RequestId, signIn.CorrelationCookie, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("GET /saml/login")]
    public async Task Login_LocalReturnUrl_TravelsAsRelayState()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/saml/login?returnUrl=%2Fconsole%2F");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("/console/", query["RelayState"].ToString());
    }

    [IntegrationTheory]
    [InlineData("https%3A%2F%2Fevil.example%2F")]
    [InlineData("%2F%2Fevil.example%2F")]
    [InlineData("%2F%5Cevil.example%2F")]
    [Endpoint("GET /saml/login")]
    public async Task Login_NonLocalReturnUrl_IsRefused(string encodedReturnUrl)
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/saml/login?returnUrl=" + encodedReturnUrl);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [IntegrationTest]
    [Endpoint("GET /saml/login")]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ResponseToIssuedRequestWithCookie_EstablishesSessionAndClearsCookie()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, signIn.RequestId);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, signIn.CorrelationCookie);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(SamlSignInTestFlow.SetsSessionCookie(response));
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith(SamlRequestCorrelation.CookieName + "=;", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_InResponseToForAnotherRequest_IsRejected()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, "_" + new string('a', 32));

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, signIn.CorrelationCookie);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(response));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_MatchingInResponseToWithoutCookie_IsRejected()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, signIn.RequestId);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(response));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_SecondResponseForTheSameRequest_IsRejected()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);
        var first = SamlSignInTestFlow.CreateResponseTo(_certificate, signIn.RequestId);
        var second = SamlSignInTestFlow.CreateResponseTo(_certificate, signIn.RequestId);

        using var accepted = await SamlSignInTestFlow.PostAcsAsync(_fixture, first, signIn.CorrelationCookie);
        using var replayed = await SamlSignInTestFlow.PostAcsAsync(_fixture, second, signIn.CorrelationCookie);
        using var resubmitted = await SamlSignInTestFlow.PostAcsAsync(_fixture, first, signIn.CorrelationCookie);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, resubmitted.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(replayed));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_UnsolicitedResponse_IsRejectedWhenSignInIsConfigured()
    {
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, inResponseTo: null);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(response));
    }
}

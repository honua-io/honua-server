// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Identity;

// A deployment with SP-initiated sign-in that also explicitly accepts IdP-initiated responses.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class SamlUnsolicitedOptInTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = SamlTestAssertions.CreateSigningCertificate();
    private readonly WebAppFixture _fixture;

    public SamlUnsolicitedOptInTests()
    {
        _fixture = SamlSignInTestFlow.CreateFixture(
            _certificate,
            new Dictionary<string, string>
            {
                ["Saml:IdpSingleSignOnServiceUrl"] = SamlSignInTestFlow.IdpSignOnUrl,
                ["Saml:AllowUnsolicitedResponses"] = "true",
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync()
    {
        _certificate.Dispose();
        return _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_UnsolicitedResponse_IsAcceptedWhenOptedIn()
    {
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, inResponseTo: null);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, cookie: null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(SamlSignInTestFlow.SetsSessionCookie(response));
    }

    [IntegrationTest]
    [Endpoint("GET /saml/login")]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ResponseToARequest_StillNeedsItsCorrelationWhenOptedIn()
    {
        var signIn = await SamlSignInTestFlow.StartAsync(_fixture);
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, signIn.RequestId);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(response));
    }
}

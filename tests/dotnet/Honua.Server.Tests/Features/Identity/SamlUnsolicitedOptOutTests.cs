// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Identity;

// An IdP-initiated-only deployment (no sign-on URL) that has explicitly refused unsolicited responses.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class SamlUnsolicitedOptOutTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = SamlTestAssertions.CreateSigningCertificate();
    private readonly WebAppFixture _fixture;

    public SamlUnsolicitedOptOutTests()
    {
        _fixture = SamlSignInTestFlow.CreateFixture(
            _certificate,
            new Dictionary<string, string> { ["Saml:AllowUnsolicitedResponses"] = "false" });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync()
    {
        _certificate.Dispose();
        return _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_UnsolicitedResponse_IsRejectedWhenOptedOut()
    {
        var samlResponse = SamlSignInTestFlow.CreateResponseTo(_certificate, inResponseTo: null);

        using var response = await SamlSignInTestFlow.PostAcsAsync(_fixture, samlResponse, cookie: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(SamlSignInTestFlow.SetsSessionCookie(response));
    }

    [IntegrationTest]
    [Endpoint("GET /saml/login")]
    public async Task Login_WithoutSignOnUrl_ReturnsNotFound()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);

        using var response = await client.GetAsync("/saml/login");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }
}

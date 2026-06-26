// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Identity;

/// <summary>
/// Integration tests for the SAML 2.0 SP endpoints (#508): SP metadata generation and the
/// Assertion Consumer Service that consumes a signed assertion and establishes a session, with
/// forged and expired assertions rejected.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public class SamlBridgeEndpointsTests : IAsyncLifetime
{
    private readonly X509Certificate2 _certificate = SamlTestAssertions.CreateSigningCertificate();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public SamlBridgeEndpointsTests()
    {
        var base64Cert = SamlTestAssertions.ToBase64Der(_certificate);
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.Audience + "/saml/acs");
                builder.UseSetting("Saml:IdpSigningCertificate", base64Cert);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        // Do not auto-follow redirects; the ACS returns 204 with a Set-Cookie on success.
        _client = _fixture.CreateClient(allowAutoRedirect: false);
    }

    public Task DisposeAsync()
    {
        _certificate.Dispose();
        return _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /saml/metadata")]
    public async Task Metadata_WhenConfigured_ReturnsSpMetadata()
    {
        var response = await _client.GetAsync("/saml/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("EntityDescriptor", body, StringComparison.Ordinal);
        Assert.Contains("AssertionConsumerService", body, StringComparison.Ordinal);
        Assert.Contains(SamlTestAssertions.Audience, body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ValidSignedAssertion_EstablishesSession()
    {
        var samlResponse = SamlTestAssertions.CreateSignedResponse(
            _certificate, "saml-user@example.com", "saml-user@example.com", "SAML User", ["editor"]);

        var response = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("honua_admin_session", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ForgedSignature_IsRejected()
    {
        var samlResponse = SamlTestAssertions.CreateTamperedResponse(_certificate, "attacker@example.com");

        var response = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.Contains("honua_admin_session", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_UnsignedAssertion_IsRejected()
    {
        var samlResponse = SamlTestAssertions.CreateUnsignedResponse("user@example.com");

        var response = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ExpiredAssertion_IsRejected()
    {
        var samlResponse = SamlTestAssertions.CreateSignedResponse(
            _certificate, "user@example.com", "u@example.com", "U", ["viewer"],
            notBefore: DateTimeOffset.UtcNow.AddMinutes(-30),
            notOnOrAfter: DateTimeOffset.UtcNow.AddMinutes(-10));

        var response = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_MissingSamlResponse_ReturnsBadRequest()
    {
        var response = await _client.PostAsync(
            "/saml/acs",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["RelayState"] = "x" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAcsAsync(string base64SamlResponse)
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = base64SamlResponse,
        });
        return await _client.PostAsync("/saml/acs", content);
    }
}

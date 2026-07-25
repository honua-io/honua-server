// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
                // #2978: SAML SP auth is an Enterprise entitlement (ADR-0024 Identity
                // Governance tier); grant it so these tests keep exercising the SAML
                // machinery itself (the gate has its own tests in IdentityEntitlementGateTests).
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.Audience + "/saml/acs");
                builder.UseSetting("Saml:SingleLogoutServiceUrl", SamlTestAssertions.Audience + "/saml/slo");
                builder.UseSetting("Saml:IdpSingleLogoutServiceUrl", "https://idp.example.com/slo");
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
    [Endpoint("GET /saml/metadata")]
    public async Task Metadata_WhenSloConfigured_AdvertisesSingleLogoutService()
    {
        var response = await _client.GetAsync("/saml/metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("SingleLogoutService", body, StringComparison.Ordinal);
        Assert.Contains(SamlTestAssertions.Audience + "/saml/slo", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_ValidSignedLogoutRequest_TerminatesSessionAndRelaysResponse()
    {
        // Seed an authenticated SAML session, then drive an IdP-initiated single logout.
        string sessionId;
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
            sessionId = await sessionStore.CreateAuthenticatedSessionAsync(
                "saml",
                "opaque-token",
                idToken: null,
                [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "slo-user@example.com" }],
                DateTimeOffset.UtcNow.AddMinutes(5),
                CancellationToken.None);
        }

        var samlRequest = SamlTestAssertions.CreateSignedLogoutRequest(_certificate, "slo-user@example.com");
        using var client = _fixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}"));

        using var sloContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = samlRequest });
        var response = await client.PostAsync("/saml/slo", sloContent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The LogoutResponse is relayed back to the IdP via an auto-submitting HTTP-POST form.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("https://idp.example.com/slo", body, StringComparison.Ordinal);
        Assert.Contains("SAMLResponse", body, StringComparison.Ordinal);

        // The local session cookie is expired.
        Assert.Contains(
            response.Headers.GetValues("Set-Cookie"),
            c => c.Contains(AdminAuthSessionStore.AuthSessionCookieName, StringComparison.Ordinal)
                 && c.Contains("01 Jan 1970", StringComparison.Ordinal));

        // The session record is removed from the store.
        await using var verifyScope = _fixture.Services.CreateAsyncScope();
        var verifyStore = verifyScope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        Assert.Null(await verifyStore.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_UnsignedLogoutRequest_IsRejected()
    {
        var samlRequest = SamlTestAssertions.CreateUnsignedLogoutRequest("attacker@example.com");

        using var sloContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = samlRequest });
        var response = await _client.PostAsync("/saml/slo", sloContent);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_MissingSamlRequest_ReturnsBadRequest()
    {
        using var sloContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["RelayState"] = "x" });
        var response = await _client.PostAsync("/saml/slo", sloContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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
    public async Task Acs_WrongAudience_IsRejected()
    {
        var samlResponse = SamlTestAssertions.CreateSignedResponse(
            _certificate,
            "wrong-audience@example.com",
            "wrong-audience@example.com",
            "Wrong Audience",
            ["viewer"],
            audience: "https://attacker.example.com/sp");

        using var response = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.Contains("honua_admin_session", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_ReplayedAssertion_IsRejected()
    {
        var samlResponse = SamlTestAssertions.CreateSignedResponse(
            _certificate,
            "replay@example.com",
            "replay@example.com",
            "Replay User",
            ["viewer"]);

        using var first = await PostAcsAsync(samlResponse);
        using var replay = await PostAcsAsync(samlResponse);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.DoesNotContain(
            replay.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
            c => c.Contains("honua_admin_session", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_MissingSamlResponse_ReturnsBadRequest()
    {
        using var acsContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["RelayState"] = "x" });
        var response = await _client.PostAsync("/saml/acs", acsContent);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostAcsAsync(string base64SamlResponse)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["SAMLResponse"] = base64SamlResponse,
        });
        return await _client.PostAsync("/saml/acs", content);
    }
}

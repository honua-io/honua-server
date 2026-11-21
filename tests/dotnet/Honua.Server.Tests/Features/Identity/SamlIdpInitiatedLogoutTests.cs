// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Identity;

// The IdP's logout request reaches the SP cross-site, so the browser sends no session cookie with it.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.IdentityManagement)]
public sealed class SamlIdpInitiatedLogoutTests : IAsyncLifetime
{
    private const string NameId = "idp-logout-user@example.com";
    private readonly X509Certificate2 _certificate = SamlTestAssertions.CreateSigningCertificate();
    private readonly WebAppFixture _fixture;

    public SamlIdpInitiatedLogoutTests()
    {
        var base64Cert = SamlTestAssertions.ToBase64Der(_certificate);
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.AcsUrl);
                builder.UseSetting("Saml:SingleLogoutServiceUrl", SamlTestAssertions.Audience + "/saml/slo");
                builder.UseSetting("Saml:IdpSigningCertificate", base64Cert);
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
    [Endpoint("POST /saml/slo")]
    public async Task Slo_WithoutTheSessionCookie_EndsTheSessionNamedBySessionIndex()
    {
        var sessionId = await SignInAsync();

        var (status, body) = await PostLogoutWithoutCookieAsync(
            SamlTestAssertions.CreateSignedLogoutRequest(_certificate, NameId));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:status:Success", body, StringComparison.Ordinal);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        Assert.Null(await sessionStore.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_NamingNoSessionWithoutTheSessionCookie_ReportsPartialLogout()
    {
        var sessionId = await SignInAsync();

        var (status, body) = await PostLogoutWithoutCookieAsync(
            SamlTestAssertions.CreateSignedLogoutRequestWithoutSessionIndex(_certificate, NameId));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:status:PartialLogout", body, StringComparison.Ordinal);
        Assert.DoesNotContain("urn:oasis:names:tc:SAML:2.0:status:Success", body, StringComparison.Ordinal);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        Assert.NotNull(await sessionStore.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_SessionIndexMatchingNoSessionWithoutTheSessionCookie_ReportsPartialLogout()
    {
        var (status, body) = await PostLogoutWithoutCookieAsync(
            SamlTestAssertions.CreateSignedLogoutRequest(_certificate, "never-signed-in@example.com"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:status:PartialLogout", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_NamingOneSubject_LeavesAnotherSubjectWithTheSameSessionIndexSignedIn()
    {
        var namedSession = await SignInAsync();
        var otherSession = await SeedIndexedSessionAsync("other-subject@example.com");

        var (status, _) = await PostLogoutWithoutCookieAsync(
            SamlTestAssertions.CreateSignedLogoutRequest(_certificate, NameId));

        Assert.Equal(HttpStatusCode.OK, status);
        await using var scope = _fixture.Services.CreateAsyncScope();
        var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        Assert.Null(await sessionStore.GetAuthenticatedSessionAsync(namedSession, CancellationToken.None));
        Assert.NotNull(await sessionStore.GetAuthenticatedSessionAsync(otherSession, CancellationToken.None));
    }

    // Signed test responses reuse fixed IDs, so only one real sign-in fits in a fixture; others are seeded.
    private async Task<string> SeedIndexedSessionAsync(string nameId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var sessionId = await scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>().CreateAuthenticatedSessionAsync(
            "saml",
            "opaque-token",
            idToken: null,
            [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "seeded-user" }],
            expiresAt,
            CancellationToken.None);
        var reference = new SamlSessionReference(nameId, SamlTestAssertions.SessionIndex);
        await scope.ServiceProvider.GetRequiredService<SamlSessionIndex>().RecordAsync(
            new SamlIndexedSession(reference, sessionId, expiresAt), CancellationToken.None);
        return sessionId;
    }

    private async Task<string> SignInAsync()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        var samlResponse = SamlTestAssertions.CreateSignedResponse(_certificate, NameId, NameId, "IdP Logout User", ["viewer"]);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse });
        using var response = await client.PostAsync("/saml/acs", content);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=", StringComparison.Ordinal));
        return cookie.Split(';', 2)[0].Split('=', 2)[1];
    }

    private async Task<(HttpStatusCode Status, string Body)> PostLogoutWithoutCookieAsync(string samlRequest)
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = samlRequest });
        using var response = await client.PostAsync("/saml/slo", content);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}

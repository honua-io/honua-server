// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Identity.Saml;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Identity;

// When the shared store cannot record what logout depends on, sign-in and logout fail closed.
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class SamlSessionStoreOutageTests
{
    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_WhenTheReplayStoreIsUnavailable_RefusesAndKeepsTheSession()
    {
        using var certificate = SamlTestAssertions.CreateSigningCertificate();
        await using var fixture = CreateReplayOutageFixture(certificate);
        await fixture.InitializeAsync();
        await using var scope = fixture.Services.CreateAsyncScope();
        var sessionStore = scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>();
        var sessionId = await sessionStore.CreateAuthenticatedSessionAsync(
            "saml",
            "opaque-token",
            idToken: null,
            [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "outage-user@example.com" }],
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None);
        using var client = fixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}"));
        var samlRequest = SamlTestAssertions.CreateSignedLogoutRequest(certificate, "outage-user@example.com");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = samlRequest });

        using var response = await client.PostAsync("/saml/slo", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotNull(await sessionStore.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/acs")]
    public async Task Acs_WhenTheSessionIndexCannotBeRecorded_IssuesNoSession()
    {
        using var certificate = SamlTestAssertions.CreateSigningCertificate();
        var sessions = ForwardingSecurityTokenStore.Create(new InMemorySecurityTokenStore());
        sessions.StoreAsync(
                ForwardingSecurityTokenStore.OfKind(SecurityTokenKind.SamlSessionIndex),
                Arg.Any<ReadOnlyMemory<byte>>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Session index unavailable")));
        await using var fixture = CreateFixture(certificate).ReplaceService(sessions);
        await fixture.InitializeAsync();
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        var samlResponse = SamlTestAssertions.CreateSignedResponse(
            certificate, "index-outage@example.com", "index-outage@example.com", "Index Outage", ["viewer"]);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = samlResponse });

        using var response = await client.PostAsync("/saml/acs", content);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=", StringComparison.Ordinal)));
        Assert.Equal(0, LiveAdminSessions(sessions));
    }

    [IntegrationTest]
    [Endpoint("POST /saml/slo")]
    public async Task Slo_WhenAnIndexedSessionCannotBeEnded_KeepsTheCallersCookieForTheRetry()
    {
        using var certificate = SamlTestAssertions.CreateSigningCertificate();
        var inner = new InMemorySecurityTokenStore();
        var sessions = ForwardingSecurityTokenStore.Create(inner);
        var removalsFail = true;
        sessions.RevokeAsync(ForwardingSecurityTokenStore.OfKind(SecurityTokenKind.SamlSessionIndex), Arg.Any<CancellationToken>())
            .Returns(call => removalsFail
                ? Task.FromException(new IOException("Session index unavailable"))
                : inner.RevokeAsync(call.ArgAt<SecurityTokenKey>(0), call.ArgAt<CancellationToken>(1)));
        await using var fixture = CreateFixture(certificate).ReplaceService(sessions);
        await fixture.InitializeAsync();
        var browserSession = await SeedSessionAsync(fixture);
        await SeedIndexedSessionAsync(fixture, "retry-user@example.com");
        var samlRequest = SamlTestAssertions.CreateSignedLogoutRequest(certificate, "retry-user@example.com");

        using var failed = await PostLogoutAsync(fixture, samlRequest, browserSession);
        removalsFail = false;
        using var retried = await PostLogoutAsync(fixture, samlRequest, browserSession);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(DeletesSessionCookie(failed));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.True(DeletesSessionCookie(retried));
        Assert.Equal(0, LiveAdminSessions(sessions));
    }

    private static bool DeletesSessionCookie(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies)
           && cookies.Any(c => c.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=", StringComparison.Ordinal)
                               && c.Contains("01 Jan 1970", StringComparison.Ordinal));

    private static async Task<string> SeedSessionAsync(WebAppFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AdminAuthSessionStore>().CreateAuthenticatedSessionAsync(
            "saml",
            "opaque-token",
            idToken: null,
            [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "seeded-user" }],
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None);
    }

    private static async Task<string> SeedIndexedSessionAsync(WebAppFixture fixture, string nameId)
    {
        var sessionId = await SeedSessionAsync(fixture);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await using var scope = fixture.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<SamlSessionIndex>().RecordAsync(
            new SamlIndexedSession(new SamlSessionReference(nameId, SamlTestAssertions.SessionIndex), sessionId, expiresAt),
            CancellationToken.None);
        return sessionId;
    }

    private static async Task<HttpResponseMessage> PostLogoutAsync(WebAppFixture fixture, string samlRequest, string sessionId)
    {
        using var client = fixture.CreateClient(
            c => c.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}"));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = samlRequest });
        return await client.PostAsync("/saml/slo", content);
    }

    private static WebAppFixture CreateFixture(X509Certificate2 certificate) =>
        CreateFixture(certificate, new InMemoryTokenReplayStore());

    private static WebAppFixture CreateReplayOutageFixture(X509Certificate2 certificate) =>
        CreateFixture(certificate, new UnavailableTokenReplayStore());

    private static WebAppFixture CreateFixture(X509Certificate2 certificate, ITokenReplayStore replays) =>
        new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ReplaceService(replays)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.Audience + "/saml/acs");
                builder.UseSetting("Saml:SingleLogoutServiceUrl", SamlTestAssertions.Audience + "/saml/slo");
                builder.UseSetting("Saml:IdpSigningCertificate", SamlTestAssertions.ToBase64Der(certificate));
            });

    // Admin sessions stored and never revoked, read from the calls the substitute received.
    private static int LiveAdminSessions(ISecurityTokenStore tokens)
    {
        var calls = tokens.ReceivedCalls()
            .Select(call => (call.GetMethodInfo().Name, Key: call.GetArguments()[0] as SecurityTokenKey?))
            .Where(call => call.Key?.Kind == SecurityTokenKind.AdminSession)
            .ToList();
        var revoked = calls.Where(call => call.Name == nameof(ISecurityTokenStore.RevokeAsync)).Select(call => call.Key).ToHashSet();
        return calls.Where(call => call.Name == nameof(ISecurityTokenStore.StoreAsync))
            .Select(call => call.Key)
            .Distinct()
            .Count(key => !revoked.Contains(key));
    }

    private sealed class UnavailableTokenReplayStore : ITokenReplayStore
    {
        public Task<TokenReplayRegistrationOutcome> TryRegisterAsync(
            TokenReplayRegistrationRequest request, CancellationToken cancellationToken)
            => Task.FromException<TokenReplayRegistrationOutcome>(new IOException("Replay store unavailable"));

        public Task<bool> TryRebindAsync(TokenReplayRebinding rebinding, CancellationToken cancellationToken)
            => Task.FromException<bool>(new IOException("Replay store unavailable"));

        public bool TryRegister(TokenReplayRegistrationRequest request)
            => throw new IOException("Replay store unavailable");
    }
}

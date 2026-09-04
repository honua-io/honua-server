// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Identity;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class AdminLogoutDependencyLossTests
{
    [IntegrationTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, true)]
    [Endpoint("POST /api/v1/admin/auth/logout")]
    [Endpoint("POST /saml/slo")]
    [Endpoint("GET /api/v1/admin/auth/session")]
    public async Task Logout_DistributedCacheOutage_Returns503UntilRevocationCanBeRetried(
        bool saml, bool warmCache, bool readsAvailable = false, bool sessionAbsent = false)
    {
        // Keep the authoritative record across an outage, just as Redis does when DEL fails.
        var backingCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var cache = Substitute.For<IDistributedCache>();
        var unavailable = false;
        cache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(call => backingCache.SetAsync(call.ArgAt<string>(0), call.ArgAt<byte[]>(1),
                call.ArgAt<DistributedCacheEntryOptions>(2), call.ArgAt<CancellationToken>(3)));
        cache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => unavailable && !readsAvailable
                ? Task.FromException<byte[]?>(new IOException("Dependency unavailable"))
                : backingCache.GetAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        cache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => unavailable
                ? Task.FromException(new IOException("Dependency unavailable"))
                : backingCache.RemoveAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));

        using var issuerMemory = new MemoryCache(new MemoryCacheOptions());
        var issuer = new AdminAuthSessionStore(issuerMemory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var otherMemory = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        var otherReplica = new AdminAuthSessionStore(otherMemory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        using var certificate = SamlTestAssertions.CreateSigningCertificate();
        await using var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .ReplaceService(store)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("Licensing:DevGrantEdition", "Enterprise");
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", SamlTestAssertions.Audience);
                builder.UseSetting("Saml:IdpEntityId", SamlTestAssertions.Issuer);
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", SamlTestAssertions.Audience + "/saml/acs");
                builder.UseSetting("Saml:IdpSigningCertificate", SamlTestAssertions.ToBase64Der(certificate));
            });
        await fixture.InitializeAsync();
        var sessionId = await issuer.CreateAuthenticatedSessionAsync("saml", "token", null,
            [new AdminAuthSessionClaim { Type = "sub", Value = "admin" }],
            DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        if (warmCache)
        {
            Assert.NotNull(await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        }
        Assert.NotNull(await otherReplica.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        using var client = fixture.CreateClient(allowAutoRedirect: false);
        client.DefaultRequestHeaders.Add("Cookie", $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}");
        var path = saml ? "/saml/slo" : "/api/v1/admin/auth/logout";
        var signedRequest = SamlTestAssertions.CreateSignedLogoutRequest(certificate, "admin");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLRequest"] = signedRequest });

        unavailable = true;
        if (sessionAbsent)
        {
            await backingCache.RemoveAsync("admin-auth:session:" + sessionId);
            using var sessionResponse = await client.GetAsync("/api/v1/admin/auth/session");
            Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
            Assert.Contains(sessionResponse.Headers.GetValues("Set-Cookie"), cookie =>
                cookie.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=;", StringComparison.Ordinal));
        }
        using var failed = await client.PostAsync(path, saml ? content : null);
        if (sessionAbsent)
        {
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            Assert.Contains(failed.Headers.GetValues("Set-Cookie"), cookie =>
                cookie.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=;", StringComparison.Ordinal));
            Assert.Null(await otherReplica.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
            return;
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("true", Assert.Single(failed.Headers.GetValues("Honua-Retryable")));
        Assert.False(failed.Headers.TryGetValues("Set-Cookie", out var cookies) &&
            cookies.Any(cookie => cookie.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=", StringComparison.Ordinal)));
        var failureBody = await failed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Dependency unavailable", failureBody, StringComparison.Ordinal);
        using var failure = JsonDocument.Parse(failureBody);
        Assert.True(failure.RootElement.GetProperty("retryable").GetBoolean());

        unavailable = false;
        // The failed response must be honest: revocation has not yet happened.
        Assert.NotNull(await otherReplica.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        using var retried = await client.PostAsync(path, saml ? content : null);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Contains(retried.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith(AdminAuthSessionStore.AuthSessionCookieName + "=", StringComparison.Ordinal));
        Assert.Null(await store.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        Assert.Null(await otherReplica.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
        using var coldMemory = new MemoryCache(new MemoryCacheOptions());
        var coldReplica = new AdminAuthSessionStore(coldMemory, NullLogger<AdminAuthSessionStore>.Instance, cache);
        Assert.Null(await coldReplica.GetAuthenticatedSessionAsync(sessionId, CancellationToken.None));
    }
}

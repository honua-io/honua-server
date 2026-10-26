// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Optional referrer (origin) and client-IP restrictions on managed API keys: the matcher,
/// enforcement on the <c>X-API-Key</c> and <c>token</c> transports, and storage shape.
/// </summary>
public sealed class ApiKeyRequestRestrictionsTests
{
    private const string BootstrapAdminPassword = "RestrictionTests-AdminPassword123!";

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com")]
    [InlineData("HTTPS://App.Example.com/", "https://app.example.com")]
    [InlineData("https://app.example.com:443", "https://app.example.com")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("https://*.example.com", "https://*.example.com")]
    [InlineData("https://[2001:db8::1]:8443", "https://[2001:db8::1]:8443")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryNormalizeReferrerPattern_ValidOrigin_IsCanonicalized(string input, string expected)
    {
        ApiKeyRequestRestrictions.TryNormalizeReferrerPattern(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("app.example.com")]
    [InlineData("ftp://app.example.com")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?x=1")]
    [InlineData("https://user@app.example.com")]
    [InlineData("https://*")]
    [InlineData("https://*.com")]
    [InlineData("https://app.*.example.com")]
    [InlineData("https://*.*.example.com")]
    [InlineData("*")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryNormalizeReferrerPattern_InvalidPattern_IsRefused(string input)
        => ApiKeyRequestRestrictions.TryNormalizeReferrerPattern(input, out _).Should().BeFalse();

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com", null, true)]
    [InlineData("https://app.example.com", null, "https://app.example.com/maps/index.html?x=1", true)]
    [InlineData("https://app.example.com", "https://evil.example.net", "https://app.example.com/", false)]
    [InlineData("https://app.example.com", "null", "https://app.example.com/", true)]
    [InlineData("https://app.example.com", "http://app.example.com", null, false)]
    [InlineData("https://app.example.com", "https://app.example.com:8443", null, false)]
    [InlineData("https://app.example.com", "https://app.example.com.evil.net", null, false)]
    [InlineData("https://*.example.com", "https://maps.example.com", null, true)]
    [InlineData("https://*.example.com", "https://a.b.example.com", null, true)]
    [InlineData("https://*.example.com", "https://example.com", null, false)]
    [InlineData("https://*.example.com", "https://badexample.com", null, false)]
    [InlineData("https://*.example.com", "http://maps.example.com", null, false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void IsReferrerAllowed_MatchesAtOriginLevel(string pattern, string? origin, string? referer, bool expected)
        => ApiKeyRequestRestrictions.IsReferrerAllowed([pattern], origin, referer).Should().Be(expected);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("null", null)]
    [InlineData("not a url", "also not")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void IsReferrerAllowed_NoUsableHeader_IsRefusedWhenAListIsConfigured(string? origin, string? referer)
    {
        ApiKeyRequestRestrictions.IsReferrerAllowed(["https://app.example.com"], origin, referer).Should().BeFalse();
        ApiKeyRequestRestrictions.IsReferrerAllowed(null, origin, referer).Should().BeTrue("no list means unrestricted");
        ApiKeyRequestRestrictions.IsReferrerAllowed([], origin, referer).Should().BeTrue("an empty list means unrestricted");
    }

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7/32")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    [InlineData("::ffff:198.51.100.4", "198.51.100.4/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryNormalizeIpRange_ValidRange_IsCanonicalized(string input, string expected)
    {
        ApiKeyRequestRestrictions.TryNormalizeIpRange(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("203.0.113.7/24")]
    [InlineData("203.0.113.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("example.com")]
    [InlineData("203.0.113.0/")]
    [InlineData("::ffff:198.51.100.0/120")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void TryNormalizeIpRange_InvalidRange_IsRefused(string input)
        => ApiKeyRequestRestrictions.TryNormalizeIpRange(input, out _).Should().BeFalse();

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.200", true)]
    [InlineData("203.0.113.0/24", "203.0.114.1", false)]
    [InlineData("203.0.113.0/24", "::ffff:203.0.113.9", true)]
    [InlineData("203.0.113.7/32", "203.0.113.7", true)]
    [InlineData("2001:db8::/32", "2001:db8:ffff::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::/32", "203.0.113.9", false)]
    [InlineData("203.0.113.0/24", "2001:db8::1", false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public void IsIpAllowed_MatchesCidrForBothFamilies(string range, string client, bool expected)
        => ApiKeyRequestRestrictions.IsIpAllowed([range], IPAddress.Parse(client)).Should().Be(expected);

    [UnitTest]
    public void IsIpAllowed_UnknownClientAddress_IsRefusedWhenAListIsConfigured()
    {
        ApiKeyRequestRestrictions.IsIpAllowed(["203.0.113.0/24"], null).Should().BeFalse();
        ApiKeyRequestRestrictions.IsIpAllowed(null, null).Should().BeTrue();
    }

    [UnitTest]
    public async Task Evaluate_NoRequestContext_PassesOnlyUnrestrictedKeys()
    {
        var store = new InMemoryAdminApiKeyStore();
        var open = await store.CreateAsync("open", ["read:test"], null, "test", CancellationToken.None);
        var restricted = await store.CreateAsync("restricted", ["read:test"], null, "test",
            new AdminApiKeyRestrictions(null, ["10.0.0.0/8"]), CancellationToken.None);

        ApiKeyRequestRestrictions.Evaluate(open.Record, null).Should().Be(ApiKeyRestrictionViolation.None);
        ApiKeyRequestRestrictions.Evaluate(restricted.Record, null).Should().Be(ApiKeyRestrictionViolation.IpRange);
    }

    [Theory]
    [InlineData("https://app.example.com", "203.0.113.9", true)]
    [InlineData("https://other.example.net", "203.0.113.9", false)]
    [InlineData(null, "203.0.113.9", false)]
    [InlineData("https://app.example.com", "198.51.100.1", false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task XApiKeyHeader_RestrictedKey_IsAcceptedOnlyFromAllowedOriginAndNetwork(
        string? origin, string clientIp, bool expected)
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await CreateRestrictedKeyAsync(store, "read:test");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = key;
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);

        var result = await AuthenticateViaXApiKeyAsync(store, context);

        result.Succeeded.Should().Be(expected);
        if (!expected)
        {
            result.Failure!.Message.Should().NotContain(key, "refusals never echo the key");
        }
    }

    [UnitTest]
    public async Task XApiKeyHeader_UnrestrictedKeyAndBootstrapKey_AreUnaffected()
    {
        var store = new InMemoryAdminApiKeyStore();
        var open = await store.CreateAsync("open", ["admin:*"], null, "test", CancellationToken.None);

        foreach (var key in new[] { open.Key, BootstrapAdminPassword })
        {
            var context = new DefaultHttpContext();
            context.Request.Headers["X-API-Key"] = key;
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");

            var result = await AuthenticateViaXApiKeyAsync(store, context);

            result.Succeeded.Should().BeTrue("restrictions apply only when configured on the key");
        }
    }

    [UnitTest]
    public async Task XApiKeyHeader_RestrictedLayerScopedWriteKey_IsEnforcedAndKeepsItsScope()
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await CreateRestrictedKeyAsync(store, "write:test/0");
        var allowed = new DefaultHttpContext();
        allowed.Request.Headers["X-API-Key"] = key;
        allowed.Request.Headers.Referer = "https://app.example.com/editor";
        allowed.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
        var refused = new DefaultHttpContext();
        refused.Request.Headers["X-API-Key"] = key;
        refused.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        var accepted = await AuthenticateViaXApiKeyAsync(store, allowed);
        var denied = await AuthenticateViaXApiKeyAsync(store, refused);

        accepted.Succeeded.Should().BeTrue();
        accepted.Principal!.IsInRole(LayerScopedWriteKey.Role).Should().BeTrue();
        denied.Succeeded.Should().BeFalse("a referrer-restricted key needs an Origin or Referer header");
    }

    [Theory]
    [InlineData("https://app.example.com/map", "203.0.113.9", true)]
    [InlineData("https://elsewhere.example.org/", "203.0.113.9", false)]
    [InlineData(null, "203.0.113.9", false)]
    [InlineData("https://app.example.com/map", "192.0.2.1", false)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task TokenQueryParameter_RestrictedKey_IsEnforced(string? referer, string clientIp, bool expected)
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await CreateRestrictedKeyAsync(store, "read:test");
        var services = new ServiceCollection()
            .AddSingleton(Dependencies(store))
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/rest/services/test/FeatureServer/0/query";
        context.Request.QueryString = new QueryString("?f=json&token=" + Uri.EscapeDataString(key));
        if (referer is not null)
        {
            context.Request.Headers.Referer = referer;
        }

        context.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);

        var result = await AuthenticateViaTokenAsync(context);

        result.Succeeded.Should().Be(expected, "the key's restrictions apply on the query-string transport too");
    }

    [UnitTest]
    public async Task ManagedKeyExchange_RestrictedFullAdminKey_IsRefused()
    {
        var store = new InMemoryAdminApiKeyStore();
        var open = await store.CreateAsync("open", ["admin:*"], null, "test", CancellationToken.None);
        var restricted = await store.CreateAsync("restricted", ["admin:*"], null, "test",
            new AdminApiKeyRestrictions(["https://app.example.com"], null), CancellationToken.None);

        ManagedKeyExchangeAuthority.ResolveTokenRoles(open.Record).Should().Equal("admin");
        ManagedKeyExchangeAuthority.ResolveTokenRoles(restricted.Record)
            .Should().BeNull("a portal token would not carry the key's restrictions");
    }

    [UnitTest]
    public async Task RecordJson_UnrestrictedKey_SerializesWithoutRestrictionProperties()
    {
        // The Redis store's conditional usage write compares these bytes with what an older
        // instance wrote, so an unrestricted record must serialize exactly as before.
        var store = new InMemoryAdminApiKeyStore();
        var open = await store.CreateAsync("open", ["read:test"], null, "test", CancellationToken.None);

        var json = JsonSerializer.Serialize(open.Record, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord);

        json.Should().NotContain("AllowedReferrers").And.NotContain("AllowedIpRanges");
        var legacy = JsonSerializer.Deserialize(json, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord)!;
        legacy.AllowedReferrers.Should().BeNull();
        legacy.AllowedIpRanges.Should().BeNull();
    }

    [UnitTest]
    public async Task RecordJson_RestrictedKey_RoundTrips()
    {
        var store = new InMemoryAdminApiKeyStore();
        var restricted = await store.CreateAsync("restricted", ["read:test"], null, "test",
            new AdminApiKeyRestrictions(["https://*.example.com"], ["203.0.113.0/24", "2001:db8::/32"]),
            CancellationToken.None);

        var json = JsonSerializer.Serialize(restricted.Record, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord);
        var read = JsonSerializer.Deserialize(json, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord)!;

        read.AllowedReferrers.Should().Equal("https://*.example.com");
        read.AllowedIpRanges.Should().Equal("203.0.113.0/24", "2001:db8::/32");
        JsonSerializer.Serialize(read, AdminApiKeyStoreJsonContext.Default.AdminApiKeyRecord).Should().Be(json);
    }

    [UnitTest]
    public async Task InMemoryStore_RotateAndValidate_PreserveRestrictions()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("restricted", ["read:test"], null, "test",
            new AdminApiKeyRestrictions(["https://app.example.com"], []), CancellationToken.None);

        created.Record.AllowedIpRanges.Should().BeNull("an empty list is stored as unrestricted");
        var rotated = await store.RotateAsync(created.Record.Id, CancellationToken.None);
        var validated = await store.ValidateAsync(rotated!.Key, CancellationToken.None);

        validated!.Record.AllowedReferrers.Should().Equal("https://app.example.com");
    }

    private static async Task<string> CreateRestrictedKeyAsync(InMemoryAdminApiKeyStore store, string grant)
    {
        var created = await store.CreateAsync("restricted-key", [grant], DateTimeOffset.UtcNow.AddMinutes(10), "test",
            new AdminApiKeyRestrictions(["https://app.example.com"], ["203.0.113.0/24"]), CancellationToken.None);
        return created.Key;
    }

    private static ApiKeyAuthenticationDependencies Dependencies(IAdminApiKeyStore store)
        => new(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                EnvironmentName = "Production",
                AdminPassword = BootstrapAdminPassword,
            }),
            secretResolver: null,
            adminApiKeyStore: store);

    private static async Task<AuthenticateResult> AuthenticateViaXApiKeyAsync(IAdminApiKeyStore store, HttpContext context)
    {
        var handler = new ApiKeyAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, Dependencies(store));
        await handler.InitializeAsync(
            new AuthenticationScheme(AuthenticationExtensions.ApiKeyScheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static async Task<AuthenticateResult> AuthenticateViaTokenAsync(HttpContext context)
    {
        // No portal token was issued, so every value falls through to the API-key check.
        var issuer = Substitute.For<IPortalTokenIssuer>();
        issuer.ValidateAsync(Arg.Any<string>(), Arg.Any<PortalTokenBinding>(), Arg.Any<CancellationToken>())
            .Returns((PortalTokenValidation?)null);
        var handler = new PortalTokenAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, issuer);
        await handler.InitializeAsync(
            new AuthenticationScheme(PortalTokenAuthenticationExtensions.PortalTokenScheme, null, typeof(PortalTokenAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static IOptionsMonitor<AuthenticationSchemeOptions> Schemes()
    {
        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        return schemes;
    }
}

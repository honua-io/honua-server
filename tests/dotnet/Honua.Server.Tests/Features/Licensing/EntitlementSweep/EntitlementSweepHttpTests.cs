// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Licensing.EntitlementSweep;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ContractTesting)]
public abstract class EntitlementSweepEditionTestsBase : IAsyncLifetime
{
    private HttpClient _adminClient = null!;

    protected abstract HonuaEdition Edition { get; }

    private readonly WebAppFixture _fixture;

    protected EntitlementSweepEditionTestsBase()
    {
        _fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(Edition)
            // #2997: pre-seed one OIDC provider so the identity.oidc-multi-provider probe's
            // create request exercises the "second provider" branch (the Enterprise gate runs
            // before the Pro identity.oidc gate for creates that would grow the store past one
            // provider, so the 402 names the multi-provider key).
            .ReplaceService<Honua.Core.Features.Identity.Abstractions.IOidcProviderStore>(CreateSeededOidcProviderStore())
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                // Mirrors IdentityEntitlementGateTests: disable the dev-auth bypass so admin
                // routes are reached through the real X-API-Key admin scheme (via AdminClient
                // below) rather than an auth shortcut that could mask gate-ordering bugs.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Saml:EntityId", "https://honua.example.com/sp");
                builder.UseSetting("Saml:IdpEntityId", "https://idp.example.com/metadata");
                builder.UseSetting("Saml:AssertionConsumerServiceUrl", "https://honua.example.com/sp/saml/acs");
                builder.UseSetting("Scim:BearerToken", EntitlementProbeRegistry.IdentityScimBearerToken);
                builder.UseSetting("Scim:OidcIssuer", "https://issuer.example.com");
                // #2958 demoted mTLS back to experimental; #2346/ADR-0058 ships branch
                // versioning experimental-off by default. Both entitlement checks sit BEHIND
                // these capability gates, so the sweep must opt in to reach them at all.
                builder.UseSetting("Capabilities:Experimental:security.mtls:Enabled", "true");
                builder.UseSetting("Capabilities:Experimental:versioning.branch:Enabled", "true");
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _adminClient = _fixture.CreateAdminClient();
    }

    private static Honua.Server.Features.Admin.Services.InMemoryOidcProviderStore CreateSeededOidcProviderStore()
    {
        var store = new Honua.Server.Features.Admin.Services.InMemoryOidcProviderStore();
        store.CreateProviderAsync(new Honua.Core.Features.Identity.Domain.OidcProviderConfiguration
        {
            Name = "sweep-seeded-provider",
            ProviderType = "Generic",
            Authority = "https://idp.example.com",
            ClientId = "sweep-client",
        }).GetAwaiter().GetResult();
        return store;
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        await _fixture.DisposeAsync();
    }

    private static bool IsBlocked(HttpStatusCode statusCode, string body)
        => statusCode == HttpStatusCode.PaymentRequired
            || (statusCode == HttpStatusCode.OK && body.Contains("\"code\":402", StringComparison.Ordinal));

    private protected async Task AssertBlockedAsync(IEnumerable<string> keys)
    {
        using var scope = new AssertionScope();
        foreach (var key in keys)
        {
            var probe = EntitlementProbeRegistry.Probes[key];
            var (statusCode, body) = await SendAsync(probe);

            IsBlocked(statusCode, body).Should().BeTrue(
                $"key '{key}' ({probe.Method} {probe.Path}) should be blocked at {Edition} edition " +
                $"(got {statusCode}: {Truncate(body)})");

            body.Should().Contain(
                $"entitlement: {key}",
                $"the entitlement-denied detail for '{key}' should name the entitlement key that blocked the request");
        }
    }

    private protected async Task AssertNotBlockedAsync(IEnumerable<string> keys)
    {
        using var scope = new AssertionScope();
        foreach (var key in keys)
        {
            var probe = EntitlementProbeRegistry.Probes[key];
            var (statusCode, body) = await SendAsync(probe);

            IsBlocked(statusCode, body).Should().BeFalse(
                $"the gate for '{key}' ({probe.Method} {probe.Path}) must not fire once {Edition} carries the " +
                "entitlement — whatever the endpoint's own validation/business logic does past that point is " +
                $"out of scope here (got {statusCode}: {Truncate(body)})");
        }
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> SendAsync(EntitlementProbe probe)
    {
        using var request = probe.CreateRequest();
        using var response = await _adminClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            return (response.StatusCode, string.Empty);
        }

        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body);
    }

    private static string Truncate(string body) => body.Length <= 200 ? body : body[..200] + "...";
}

public sealed class CommunityEditionEntitlementSweepTests : EntitlementSweepEditionTestsBase
{
    protected override HonuaEdition Edition => HonuaEdition.Community;

    [IntegrationTest]
    public Task AllProbes_AreBlockedAtCommunity()
        => AssertBlockedAsync(EntitlementProbeRegistry.Probes.Keys);
}

public sealed class ProEditionEntitlementSweepTests : EntitlementSweepEditionTestsBase
{
    protected override HonuaEdition Edition => HonuaEdition.Pro;

    private static IEnumerable<string> ProKeys() =>
        EntitlementProbeRegistry.GatedKeys
            .Where(f => f.MinimumEdition == HonuaEdition.Pro && EntitlementProbeRegistry.Probes.ContainsKey(f.Key))
            .Select(f => f.Key);

    private static IEnumerable<string> EnterpriseKeys() =>
        EntitlementProbeRegistry.GatedKeys
            .Where(f => f.MinimumEdition == HonuaEdition.Enterprise && EntitlementProbeRegistry.Probes.ContainsKey(f.Key))
            .Select(f => f.Key);

    [IntegrationTest]
    public Task ProGatedProbes_AreNotBlockedAtPro() => AssertNotBlockedAsync(ProKeys());

    [IntegrationTest]
    public Task EnterpriseGatedProbes_AreStillBlockedAtPro() => AssertBlockedAsync(EnterpriseKeys());
}

public sealed class EnterpriseEditionEntitlementSweepTests : EntitlementSweepEditionTestsBase
{
    protected override HonuaEdition Edition => HonuaEdition.Enterprise;

    [IntegrationTest]
    public Task AllProbes_AreNotBlockedAtEnterprise()
        => AssertNotBlockedAsync(EntitlementProbeRegistry.Probes.Keys);
}

public sealed class EntitlementSweepHttpCoverageTests
{
    [UnitTest]
    public void EveryProbe_HasAKnownMinimumEdition()
    {
        var gatedByKey = EntitlementProbeRegistry.GatedKeys.ToDictionary(f => f.Key, f => f.MinimumEdition);

        foreach (var key in EntitlementProbeRegistry.Probes.Keys)
        {
            gatedByKey.Should().ContainKey(key);
            (gatedByKey[key] == HonuaEdition.Pro || gatedByKey[key] == HonuaEdition.Enterprise).Should().BeTrue(
                $"probe '{key}' must belong to a Pro or Enterprise key to be exercised by the edition test classes");
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Licensing.EntitlementSweep;

/// <summary>
/// Local entitlement sweep (#2980): catalog-generated 402/200 matrix across every
/// license-gated HTTP endpoint that has a probeable surface (see
/// <see cref="EntitlementProbeRegistry"/>).
///
/// Each edition gets its own dedicated test class with a single, instance-field
/// <see cref="WebAppFixture"/> — mirroring every other integration test in this project
/// (e.g. <c>SpatialAnalyticsRestTests</c>, <c>VisibilityAnalysisEndpointTests</c>) — one
/// Testcontainers Postgres per edition, reused across every probe in that edition rather
/// than per probe (spinning ~90 individual containers would be prohibitively slow).
///
/// <b>The "blocked" check is not simply <c>StatusCode == 402</c>.</b> GeoServices-classified
/// routes (<c>/rest/services/...</c>, matched by <c>ProtocolRequestClassifier.IsGeoServices</c>)
/// follow the documented Esri GeoServices REST convention (see
/// <c>StandardErrorResponseFormatter.FormatGeoServicesError</c>, "PA-070/PA-117: ALL responses
/// — including errors — use HTTP 200 OK; the real status is signalled exclusively through the
/// JSON body <c>{"error":{"code":402,...}}</c>"). An entitlement 402 for
/// <c>analytics.clustering</c>/<c>editing.featureserver-edits</c>/etc. is therefore a real HTTP
/// 200 carrying <c>"code":402</c> in the body, by design — not a gap. <see cref="EntitlementSweepEditionTestsBase.IsBlocked"/>
/// treats EITHER shape (a literal 402, or a 200 with an embedded GeoServices <c>code:402</c>) as
/// "blocked", so the sweep doesn't misreport this convention as an enforcement failure. This was
/// confirmed by direct repro: an isolated single-request test against
/// <c>analytics.clustering</c> printed literal <c>Status: OK</c> with a body of
/// <c>{"error":{"code":402,...}}</c> — the gate fires correctly; only the wire shape differs
/// from the non-GeoServices 402 convention used elsewhere (SAML/SCIM/mTLS/elevation/etc.).
///
/// Each probe's expected-blocked/expected-open assertions are accumulated in one
/// <see cref="AssertionScope"/> per test so a single run reports every failing key at once
/// instead of stopping at the first.
/// </summary>
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
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                // Mirrors IdentityEntitlementGateTests: disable the dev-auth bypass so admin
                // routes are reached through the real X-API-Key admin scheme (via AdminClient
                // below) rather than an auth shortcut that could mask gate-ordering bugs.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Saml:Enabled", "true");
                builder.UseSetting("Scim:BearerToken", EntitlementProbeRegistry.IdentityScimBearerToken);
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

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// A request is "blocked" by the entitlement gate when EITHER a literal HTTP 402 was
    /// returned (every non-GeoServices surface), OR the response is the GeoServices-classified
    /// 200-with-embedded-error-code convention carrying <c>"code":402</c> (see the class doc
    /// comment). Both are the same gate firing — only the wire shape differs by protocol.
    /// </summary>
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

    /// <summary>
    /// Sends the probe with <see cref="HttpCompletionOption.ResponseHeadersRead"/> and only
    /// fully drains the body when it is NOT an open streaming response. SSE/chunked long-lived
    /// transports never terminate their body on their own — <c>ai.spec-apply</c>'s success path
    /// is a real Server-Sent-Events stream that stays open, so <c>ReadAsStringAsync</c> against
    /// it would hang for the full HttpClient timeout instead of returning. Every probe's
    /// "blocked" signal (a literal 402, or a GeoServices 200 wrapping <c>code:402</c>) is fully
    /// framed in the response headers/status either way, so skipping the body read for a
    /// genuinely open stream never hides a real gate failure: a blocked SSE request 402s before
    /// the stream ever opens, with a normal (non-event-stream) content type.
    /// </summary>
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

/// <summary>Community edition: every probe (Pro or Enterprise) must 402.</summary>
public sealed class CommunityEditionEntitlementSweepTests : EntitlementSweepEditionTestsBase
{
    protected override HonuaEdition Edition => HonuaEdition.Community;

    [IntegrationTest]
    public Task AllProbes_AreBlockedAtCommunity()
        => AssertBlockedAsync(EntitlementProbeRegistry.Probes.Keys);
}

/// <summary>
/// Pro edition: Pro-tier probes must open up; Enterprise-tier probes must still 402
/// ("Pro-is-not-enough", mirrors the #2978 SAML/SCIM shape).
/// </summary>
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

/// <summary>Enterprise edition: every probe (Pro or Enterprise) must open up.</summary>
public sealed class EnterpriseEditionEntitlementSweepTests : EntitlementSweepEditionTestsBase
{
    protected override HonuaEdition Edition => HonuaEdition.Enterprise;

    [IntegrationTest]
    public Task AllProbes_AreNotBlockedAtEnterprise()
        => AssertNotBlockedAsync(EntitlementProbeRegistry.Probes.Keys);
}

/// <summary>
/// Guards that every probe is actually exercised by one of the three edition classes above
/// (a probe whose key's <c>MinimumEdition</c> somehow isn't Pro or Enterprise would silently
/// never run — <see cref="EntitlementSweepCoverageTests"/> is the broader coverage
/// guarantee this narrower check complements).
/// </summary>
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

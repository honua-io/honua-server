// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Entitlement-gate tests for the alerts/channels surface (#2998). The alert edition policy is
/// license-derived: <c>alerts.enter-exit</c>/<c>alerts.evaluation</c>/<c>channels.webhook</c>
/// are Pro; <c>alerts.dwell</c>/<c>alerts.threshold</c> and the remaining channels are
/// Enterprise. <c>Alerts:Edition</c> is a downward-only cap — it can restrict below the
/// license-derived tier but never grants features the license does not include.
/// </summary>
/// <remarks>
/// Naming constraint (ADR-0037): test methods here must say "Edition", never "License".
/// "Server Features Admin and Console" in .github/ci-shards.json is the only shard claiming
/// <c>Honua.Server.Tests.Features.Alerts.*</c>, and it carries
/// <c>FullyQualifiedName!~License</c> so that <c>Features.Admin</c> licensing classes route to
/// "Server Features Admin Platform and Governance" instead. A "License" token anywhere in an
/// Alerts test's fully-qualified name therefore drops that test out of every CI shard and it
/// silently never runs.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertEntitlementGateTests
{
    private const string RulesRoute = "/api/v1/admin/alerts/rules";

    private static WebAppFixture CreateFixture(HonuaEdition edition, AlertEdition? alertsEditionCap = null)
    {
        var fixture = new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(edition)
            .ConfigureWebHost(builder => builder.UseEnvironment("Test"));

        if (alertsEditionCap is { } cap)
        {
            // The cap is injected as the bound options instance rather than as configuration:
            // WebAppFixture's web-host callbacks do not surface an "Alerts" configuration
            // section to the app's options binding, so a config-based cap silently stays null
            // here. The configuration -> AlertOptions.Edition binding itself is covered
            // directly by AlertOptionsBindingTests.
            fixture = fixture.ReplaceService<IOptions<AlertOptions>>(
                Options.Create(new AlertOptions { Edition = cap }));
        }

        return fixture;
    }

    private static object RulePayload(string triggerType, string channel, string conditionsJson = "{}") => new
    {
        serviceId = $"gate-{Guid.NewGuid():N}",
        // Must be positive: rule-shape validation (TryCreateRuleDefinition) runs before the
        // entitlement gate, so layerId 0 would 400 and mask the 402 under test.
        layerId = 1,
        zoneId = triggerType is "enter" or "exit" or "dwell" ? (long?)1 : null,
        ruleName = $"{triggerType} gate probe",
        triggerType,
        conditionsJson,
        cooldownSeconds = 30,
        severity = "warning",
        editionRequired = "pro",
        channels = new[] { channel },
        isActive = true,
    };

    private static async Task AssertBlockedAsync(HttpClient client, object payload, string expectedKey)
    {
        using var response = await client.PostAsJsonAsync(RulesRoute, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.PaymentRequired,
            $"Expected 402 for '{expectedKey}' but got {(int)response.StatusCode}: {body}");
        Assert.Contains($"entitlement: {expectedKey}", body);
    }

    /// <summary>
    /// A denial caused only by the downward <c>Alerts:Edition</c> cap must not masquerade as a
    /// missing entitlement: the license grants the feature, so a 402 naming the key would send an
    /// operator to buy or reinstall a license they already own. It must surface as the ordinary
    /// configured-edition validation failure instead.
    /// </summary>
    private static async Task AssertBlockedByEditionCapAsync(HttpClient client, object payload, string expectedFragment)
    {
        using var response = await client.PostAsJsonAsync(RulesRoute, payload);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest,
            $"Expected a configured-edition 400 but got {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("entitlement: ", body, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, body, StringComparison.Ordinal);
    }

    private static async Task AssertNotBlockedAsync(HttpClient client, object payload)
    {
        // The gate must not fire; downstream validation (nonexistent zone, unconfigured
        // channel) may still 400, which is out of scope here.
        using var response = await client.PostAsJsonAsync(RulesRoute, payload);
        Assert.NotEqual(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("entitlement: ", body, StringComparison.Ordinal);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task CommunityEdition_ProAlertFeatures_AreBlockedWithEntitlementDetail()
    {
        var fixture = CreateFixture(HonuaEdition.Community);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            await AssertBlockedAsync(client, RulePayload("enter", "webhook"), "alerts.enter-exit");
            await AssertBlockedAsync(client, RulePayload("enter", "webhook"), "channels.webhook");

            // On-demand rule evaluation (POST /rules/test) is the alerts.evaluation surface.
            using var testResponse = await client.PostAsJsonAsync(
                RulesRoute + "/test", new { rule = RulePayload("enter", "webhook") });
            Assert.Equal(HttpStatusCode.PaymentRequired, testResponse.StatusCode);
            var body = await testResponse.Content.ReadAsStringAsync();
            Assert.Contains("alerts.evaluation", body);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task ProEdition_AllowsProFeatures_BlocksEnterpriseFeatures()
    {
        var fixture = CreateFixture(HonuaEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            // Pro features open up.
            await AssertNotBlockedAsync(client, RulePayload("enter", "webhook"));
            using var testResponse = await client.PostAsJsonAsync(
                RulesRoute + "/test", new { rule = RulePayload("enter", "webhook") });
            Assert.Equal(HttpStatusCode.OK, testResponse.StatusCode);

            // Enterprise triggers/channels stay blocked ("Pro is not enough").
            await AssertBlockedAsync(
                client,
                RulePayload("dwell", "webhook", conditionsJson: "{\"dwellSeconds\":60}"),
                "alerts.dwell");
            await AssertBlockedAsync(
                client,
                RulePayload("threshold", "webhook", conditionsJson: "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}"),
                "alerts.threshold");
            await AssertBlockedAsync(client, RulePayload("enter", "email"), "channels.email");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task EnterpriseEdition_AllowsEnterpriseFeatures()
    {
        var fixture = CreateFixture(HonuaEdition.Enterprise);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            await AssertNotBlockedAsync(client, RulePayload("dwell", "webhook", conditionsJson: "{\"dwellSeconds\":60}"));
            await AssertNotBlockedAsync(client, RulePayload("enter", "email"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task EnterpriseEdition_WithProCap_BlocksEnterpriseFeaturesOnly()
    {
        // Alerts:Edition=Pro is a downward cap: Enterprise features are blocked even though the
        // license grants them, while Pro features keep working. Because the license does include
        // alerts.dwell/channels.email here, the block must be reported as a configured-edition
        // failure, not as a 402 claiming the license lacks those keys.
        var fixture = CreateFixture(HonuaEdition.Enterprise, alertsEditionCap: AlertEdition.Pro);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            // The cap only means anything if it actually reached the policy: assert it first so
            // a wiring regression cannot masquerade as a passing gate test.
            Assert.Equal(
                AlertEdition.Pro,
                fixture.GetService<IOptions<AlertOptions>>().Value.Edition);

            await AssertBlockedByEditionCapAsync(
                client,
                RulePayload("dwell", "webhook", conditionsJson: "{\"dwellSeconds\":60}"),
                "configured alert edition cap");
            await AssertBlockedByEditionCapAsync(
                client,
                RulePayload("enter", "email"),
                "configured alert edition cap");
            await AssertNotBlockedAsync(client, RulePayload("enter", "webhook"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task CommunityEdition_WithEnterpriseCap_DoesNotGrantFeatures()
    {
        // Alerts:Edition can never grant upward: with a Community license, setting the knob to
        // Enterprise still leaves every alert feature blocked (the license wins).
        var fixture = CreateFixture(HonuaEdition.Community, alertsEditionCap: AlertEdition.Enterprise);
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            await AssertBlockedAsync(client, RulePayload("enter", "webhook"), "alerts.enter-exit");
            await AssertBlockedAsync(
                client,
                RulePayload("dwell", "webhook", conditionsJson: "{\"dwellSeconds\":60}"),
                "alerts.dwell");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

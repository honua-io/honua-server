// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Entitlement-gate tests for the alerts/channels surface (#2998). The alert edition policy is
/// license-derived: <c>alerts.enter-exit</c>/<c>alerts.evaluation</c>/<c>channels.webhook</c>
/// are Pro; <c>alerts.dwell</c>/<c>alerts.threshold</c> and the remaining channels are
/// Enterprise. <c>Alerts:Edition</c> is a downward-only cap — it can restrict below the
/// license-derived tier but never grants features the license does not include.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertEntitlementGateTests
{
    private const string RulesRoute = "/api/v1/admin/alerts/rules";

    private static WebAppFixture CreateFixture(HonuaEdition edition, string? alertsEditionCap = null)
        => new WebAppFixture()
            .UseSeed("tests/seed/server.yaml")
            .WithTestLicense(edition)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                if (alertsEditionCap is not null)
                {
                    builder.UseSetting("Alerts:Edition", alertsEditionCap);
                }
            });

    private static object RulePayload(string triggerType, string channel, string conditionsJson = "{}") => new
    {
        serviceId = $"gate-{Guid.NewGuid():N}",
        layerId = 0,
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
        Assert.Equal(HttpStatusCode.PaymentRequired, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains($"entitlement: {expectedKey}", body);
    }

    private static async Task AssertNotBlockedAsync(HttpClient client, object payload)
    {
        // The gate must not fire; downstream validation (nonexistent zone, unconfigured
        // channel) may still 400, which is out of scope here.
        using var response = await client.PostAsJsonAsync(RulesRoute, payload);
        Assert.NotEqual(HttpStatusCode.PaymentRequired, response.StatusCode);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task CommunityLicense_ProAlertFeatures_AreBlockedWithEntitlementDetail()
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
    public async Task ProLicense_AllowsProFeatures_BlocksEnterpriseFeatures()
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
    public async Task EnterpriseLicense_AllowsEnterpriseFeatures()
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
    public async Task EnterpriseLicense_WithProCap_BlocksEnterpriseFeaturesOnly()
    {
        // Alerts:Edition=Pro is a downward cap: Enterprise features are blocked even though
        // the license grants them, while Pro features keep working.
        var fixture = CreateFixture(HonuaEdition.Enterprise, alertsEditionCap: "Pro");
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();

            await AssertBlockedAsync(
                client,
                RulePayload("dwell", "webhook", conditionsJson: "{\"dwellSeconds\":60}"),
                "alerts.dwell");
            await AssertBlockedAsync(client, RulePayload("enter", "email"), "channels.email");
            await AssertNotBlockedAsync(client, RulePayload("enter", "webhook"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task CommunityLicense_WithEnterpriseCap_DoesNotGrantFeatures()
    {
        // Alerts:Edition can never grant upward: with a Community license, setting the knob to
        // Enterprise still leaves every alert feature blocked (the license wins).
        var fixture = CreateFixture(HonuaEdition.Community, alertsEditionCap: "Enterprise");
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

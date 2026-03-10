// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertAdminEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = CreateFixture(
        allowedChannels: [AlertChannelType.Webhook],
        configuredChannels: [AlertChannelType.Webhook]);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    public async Task ListZones_WithServiceFilter_ReturnsSuccessEnvelope()
    {
        var serviceId = $"zones-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/v1/admin/alerts/zones?serviceId={serviceId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    public async Task ZoneCrud_CreateUpdateDelete_CompletesLifecycle()
    {
        var serviceId = $"zones-{Guid.NewGuid():N}";
        var createPayload = new
        {
            serviceId,
            zoneName = "Honolulu Harbor",
            wkt = "POLYGON((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29))",
            srid = 4326,
            metadata = new Dictionary<string, string?> { ["owner"] = "operations" },
            isActive = true
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/alerts/zones", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var createdZone = createDocument.RootElement.GetProperty("data");
        var zoneId = createdZone.GetProperty("zoneId").GetInt64();
        zoneId.Should().BeGreaterThan(0);
        createdZone.GetProperty("wkt").GetString().Should().Contain("MULTIPOLYGON");

        var updatePayload = new
        {
            serviceId,
            zoneName = "Honolulu Harbor Updated",
            wkt = "MULTIPOLYGON(((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29)))",
            srid = 4326,
            metadata = new Dictionary<string, string?> { ["owner"] = "ops-team" },
            isActive = false
        };

        var updateResponse = await _client.PutAsJsonAsync($"/api/v1/admin/alerts/zones/{zoneId}", updatePayload);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var updateDocument = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        updateDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var updatedZone = updateDocument.RootElement.GetProperty("data");
        updatedZone.GetProperty("zoneName").GetString().Should().Be("Honolulu Harbor Updated");
        updatedZone.GetProperty("isActive").GetBoolean().Should().BeFalse();

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/alerts/zones/{zoneId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var deleteDocument = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync());
        deleteDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/rules")]
    public async Task ListRules_WithServiceAndLayerFilter_ReturnsSuccessEnvelope()
    {
        var serviceId = $"rules-{Guid.NewGuid():N}";
        var response = await _client.GetAsync($"/api/v1/admin/alerts/rules?serviceId={serviceId}&layerId=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task RuleCrud_CreateUpdateDelete_CompletesLifecycle()
    {
        var serviceId = $"rules-{Guid.NewGuid():N}";
        var createPayload = new
        {
            serviceId,
            layerId = 1,
            zoneId = (long?)null,
            ruleName = "Harbor Entry",
            triggerType = "enter",
            conditionsJson = "{\"speedKmh\": 30}",
            cooldownSeconds = 60,
            severity = "warning",
            editionRequired = "pro",
            channels = new[] { "webhook" },
            isActive = true
        };

        var createResponse = await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules", createPayload);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var createDocument = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        createDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var createdRule = createDocument.RootElement.GetProperty("data");
        var ruleId = createdRule.GetProperty("ruleId").GetInt64();
        ruleId.Should().BeGreaterThan(0);

        var updatePayload = new
        {
            serviceId,
            layerId = 1,
            zoneId = (long?)null,
            ruleName = "Harbor Exit",
            triggerType = "exit",
            conditionsJson = "{\"speedKmh\": 20}",
            cooldownSeconds = 120,
            severity = "critical",
            editionRequired = "pro",
            channels = new[] { "webhook" },
            isActive = false
        };

        var updateResponse = await _client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{ruleId}", updatePayload);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var updateDocument = JsonDocument.Parse(await updateResponse.Content.ReadAsStringAsync());
        updateDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        var updatedRule = updateDocument.RootElement.GetProperty("data");
        updatedRule.GetProperty("ruleName").GetString().Should().Be("Harbor Exit");
        updatedRule.GetProperty("triggerType").GetString().Should().Be("exit");
        updatedRule.GetProperty("isActive").GetBoolean().Should().BeFalse();

        var deleteResponse = await _client.DeleteAsync($"/api/v1/admin/alerts/rules/{ruleId}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var deleteDocument = JsonDocument.Parse(await deleteResponse.Content.ReadAsStringAsync());
        deleteDocument.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task CreateRule_WithUnconfiguredChannel_ReturnsBadRequest()
    {
        var fixture = CreateFixture(
            allowedChannels: [AlertChannelType.MicrosoftTeams],
            configuredChannels: []);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var payload = new
            {
                serviceId = $"rules-{Guid.NewGuid():N}",
                layerId = 1,
                zoneId = (long?)null,
                ruleName = "Teams Alert",
                triggerType = "enter",
                conditionsJson = "{}",
                cooldownSeconds = 30,
                severity = "warning",
                editionRequired = "enterprise",
                channels = new[] { "microsoft_teams" },
                isActive = true
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/alerts/rules", payload);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("message").GetString().Should().Contain("not configured");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(
        IReadOnlyCollection<AlertChannelType> allowedChannels,
        IReadOnlyCollection<AlertChannelType> configuredChannels)
    {
        return new WebAppFixture().ReplaceService<IAlertEditionPolicy>(
            new TestAlertEditionPolicy(allowedChannels, configuredChannels));
    }

    private sealed class TestAlertEditionPolicy : IAlertEditionPolicy
    {
        private readonly HashSet<AlertChannelType> _allowedChannels;
        private readonly HashSet<AlertChannelType> _configuredChannels;

        public TestAlertEditionPolicy(
            IReadOnlyCollection<AlertChannelType> allowedChannels,
            IReadOnlyCollection<AlertChannelType> configuredChannels)
        {
            _allowedChannels = allowedChannels.ToHashSet();
            _configuredChannels = configuredChannels.ToHashSet();
        }

        public bool IsRuleAllowed(AlertRuleDefinition rule) => true;

        public bool IsChannelAllowed(AlertChannelType channelType) => _allowedChannels.Contains(channelType);

        public bool IsChannelConfigured(AlertChannelType channelType) => _configuredChannels.Contains(channelType);
    }
}

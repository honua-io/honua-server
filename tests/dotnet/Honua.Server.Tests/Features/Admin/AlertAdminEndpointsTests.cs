// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
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
    [Endpoint("GET /api/v1/admin/alerts/zones/{zoneId}")]
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

        var getResponse = await _client.GetAsync($"/api/v1/admin/alerts/zones/{zoneId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()))
        {
            getDocument.RootElement.GetProperty("data").GetProperty("zoneId").GetInt64().Should().Be(zoneId);
        }

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
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}/enabled")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task RuleCrud_CreateUpdateDelete_CompletesLifecycle()
    {
        var serviceId = $"rules-{Guid.NewGuid():N}";
        var zoneId = await CreateZoneAsync(serviceId);
        var createPayload = new
        {
            serviceId,
            layerId = 1,
            zoneId = (long?)zoneId,
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

        var getResponse = await _client.GetAsync($"/api/v1/admin/alerts/rules/{ruleId}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var getDocument = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync()))
        {
            getDocument.RootElement.GetProperty("data").GetProperty("ruleId").GetInt64().Should().Be(ruleId);
        }

        var updatePayload = new
        {
            serviceId,
            layerId = 1,
            zoneId = (long?)zoneId,
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

        var enableResponse = await _client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{ruleId}/enabled", new { enabled = true });
        enableResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var enableDocument = JsonDocument.Parse(await enableResponse.Content.ReadAsStringAsync()))
        {
            enableDocument.RootElement.GetProperty("data").GetProperty("isActive").GetBoolean().Should().BeTrue();
        }

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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task CreateRule_WithZoneIdOnThresholdRule_ReturnsBadRequestWithoutPersisting()
    {
        var serviceId = $"rules-{Guid.NewGuid():N}";
        var zoneId = await CreateZoneAsync(serviceId);
        var payload = new
        {
            serviceId,
            layerId = 1,
            zoneId = (long?)zoneId,
            ruleName = "Zone Scoped Threshold",
            triggerType = "threshold",
            conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
            cooldownSeconds = 30,
            severity = "warning",
            editionRequired = "pro",
            channels = Array.Empty<string>(),
            isActive = true
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
        {
            document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            document.RootElement.GetProperty("message").GetString().Should().Contain("ZoneId");
        }

        var listResponse = await _client.GetAsync($"/api/v1/admin/alerts/rules?serviceId={serviceId}&layerId=1");
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDocument.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    public async Task UpdateZone_NonexistentZoneId_Returns404()
    {
        var updatePayload = new
        {
            serviceId = $"zones-{Guid.NewGuid():N}",
            zoneName = "Ghost Zone",
            wkt = "POLYGON((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29))",
            srid = 4326,
            isActive = true
        };

        var response = await _client.PutAsJsonAsync("/api/v1/admin/alerts/zones/999999999", updatePayload);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    public async Task DeleteZone_NonexistentZoneId_Returns404()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/alerts/zones/999999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    public async Task CreateZone_InvalidWkt_Returns400()
    {
        var payload = new
        {
            serviceId = $"zones-{Guid.NewGuid():N}",
            zoneName = "Bad WKT Zone",
            wkt = "NOT_A_VALID_WKT_STRING",
            srid = 4326,
            isActive = true
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/zones", payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task UpdateRule_NonexistentRuleId_Returns404()
    {
        var updatePayload = new
        {
            serviceId = $"rules-{Guid.NewGuid():N}",
            layerId = 1,
            ruleName = "Ghost Rule",
            triggerType = "threshold",
            conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
            cooldownSeconds = 60,
            severity = "warning",
            editionRequired = "pro",
            channels = new[] { "webhook" },
            isActive = true
        };

        var response = await _client.PutAsJsonAsync("/api/v1/admin/alerts/rules/999999999", updatePayload);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task DeleteRule_NonexistentRuleId_Returns404()
    {
        var response = await _client.DeleteAsync("/api/v1/admin/alerts/rules/999999999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task TestRule_WithDraftZoneAndDisabledRule_ReturnsValidationStatesWithoutPersisting()
    {
        var serviceId = $"rule-test-{Guid.NewGuid():N}";
        var payload = new
        {
            rule = new
            {
                serviceId,
                layerId = 1,
                zoneId = (long?)null,
                ruleName = "Draft Entry",
                triggerType = "enter",
                conditionsJson = "{}",
                cooldownSeconds = 30,
                severity = "warning",
                editionRequired = "pro",
                channels = new[] { "webhook" },
                isActive = false
            },
            zone = new
            {
                serviceId,
                zoneName = "Draft Zone",
                wkt = "POLYGON((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29))",
                srid = 4326,
                isActive = true
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules/test", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("isValid").GetBoolean().Should().BeTrue();
        data.GetProperty("deliveryChannels")[0].GetProperty("status").GetString().Should().Be("disabled");

        var listResponse = await _client.GetAsync($"/api/v1/admin/alerts/rules?serviceId={serviceId}&layerId=1");
        using var listDocument = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        listDocument.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task TestRule_WithInvalidThresholdExpression_ReturnsValidationError()
    {
        var payload = new
        {
            rule = new
            {
                serviceId = $"rule-test-{Guid.NewGuid():N}",
                layerId = 1,
                zoneId = (long?)null,
                ruleName = "Draft Threshold",
                triggerType = "threshold",
                conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\"between\",\"value\":30}",
                cooldownSeconds = 30,
                severity = "warning",
                editionRequired = "pro",
                channels = Array.Empty<string>(),
                isActive = true
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules/test", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("isValid").GetBoolean().Should().BeFalse();
        data.GetProperty("errors")[0].GetString().Should().Contain("operator");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task TestRule_WithUnauthorizedChannel_ReturnsChannelState()
    {
        var fixture = CreateFixture(
            allowedChannels: [],
            configuredChannels: [AlertChannelType.Webhook]);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var payload = new
            {
                rule = new
                {
                    serviceId = $"rule-test-{Guid.NewGuid():N}",
                    layerId = 1,
                    zoneId = (long?)null,
                    ruleName = "Draft Threshold",
                    triggerType = "threshold",
                    conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
                    cooldownSeconds = 30,
                    severity = "warning",
                    editionRequired = "pro",
                    channels = new[] { "webhook" },
                    isActive = true
                }
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/alerts/rules/test", payload);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("isValid").GetBoolean().Should().BeFalse();
            data.GetProperty("deliveryChannels")[0].GetProperty("status").GetString().Should().Be("unauthorized");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task TestRule_WithZoneIdOnThresholdRule_ReturnsValidationError()
    {
        var payload = new
        {
            rule = new
            {
                serviceId = $"rule-test-{Guid.NewGuid():N}",
                layerId = 1,
                zoneId = (long?)999999999,
                ruleName = "Draft Zone Scoped Threshold",
                triggerType = "threshold",
                conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
                cooldownSeconds = 30,
                severity = "warning",
                editionRequired = "pro",
                channels = Array.Empty<string>(),
                isActive = true
            }
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules/test", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("isValid").GetBoolean().Should().BeFalse();
        data.GetProperty("errors")[0].GetString().Should().Contain("ZoneId");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    public async Task CreateRule_RecordsAuditEvidence()
    {
        var audit = new CapturingAuditLog();
        var store = new StubAlertAdminStore();
        var fixture = CreateStubbedFixture(store, audit, new StubAlertEventQuery());

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var payload = new
            {
                serviceId = $"audit-{Guid.NewGuid():N}",
                layerId = 1,
                ruleName = "Audit Threshold",
                triggerType = "threshold",
                conditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
                cooldownSeconds = 30,
                severity = "warning",
                editionRequired = "pro",
                channels = Array.Empty<string>(),
                isActive = true
            };

            var response = await client.PostAsJsonAsync("/api/v1/admin/alerts/rules", payload);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            audit.Recorded.Should().ContainSingle();
            audit.Recorded[0].Action.Should().Be("alert_rule.create");
            audit.Recorded[0].ResourceType.Should().Be("alert_rule");
            audit.Recorded[0].CorrelationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/health")]
    public async Task GetRuleHealth_WithRateLimitedDelivery_ReturnsChannelState()
    {
        var store = new StubAlertAdminStore();
        store.Rules[42] = new AlertRuleDefinition
        {
            RuleId = 42,
            ServiceId = "svc",
            LayerId = 1,
            RuleName = "Webhook Rule",
            TriggerType = AlertTriggerType.Threshold,
            ConditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
            CooldownSeconds = 30,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray.Create(AlertChannelType.Webhook),
            IsActive = true
        };
        store.Health = new AlertRuleHealthSnapshot
        {
            RuleId = 42,
            ActiveIncidentCount = 1,
            RecentTriggerCount = 1,
            CoolingDownFeatureCount = 0,
            DeliveryFailureCount = 1,
            DeadLetterCount = 0,
            LinkedEventIds = ImmutableArray.Create(101L),
            DeliveryChannels = ImmutableArray.Create(new AlertRuleDeliveryHealth
            {
                ChannelType = AlertChannelType.Webhook,
                PendingCount = 0,
                ProcessingCount = 0,
                DeliveredCount = 0,
                FailedCount = 1,
                DeadLetterCount = 0,
                LastError = "Webhook responded with 429."
            })
        };

        var query = new StubAlertEventQuery
        {
            Page = new AlertEventPage
            {
                Items = new[]
                {
                    new AlertEventSummary
                    {
                        EventId = 101,
                        RuleId = 42,
                        ServiceId = "svc",
                        LayerId = 1,
                        ObjectId = 5,
                        TriggerType = AlertTriggerType.Threshold,
                        Severity = AlertSeverity.Warning,
                        OccurredAt = DateTimeOffset.UtcNow,
                        IncidentStatus = AlertIncidentStatus.Started,
                        IncidentDurationMs = 0,
                        LifecycleStatus = AlertLifecycleStatus.Open
                    }
                }
            }
        };

        var fixture = CreateStubbedFixture(store, new CapturingAuditLog(), query);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/alerts/rules/42/health");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            data.GetProperty("activeIncidentCount").GetInt32().Should().Be(1);
            data.GetProperty("deliveryChannels")[0].GetProperty("status").GetString().Should().Be("rate_limited");
            data.GetProperty("recentTriggers")[0].GetProperty("resourceRef").GetString().Should().Be("alert/101");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/health")]
    public async Task GetRuleHealth_WithUnavailableDeliveryChannel_ReturnsPolicyState()
    {
        await AssertHealthChannelStatusAsync(
            allowedChannels: [],
            configuredChannels: [AlertChannelType.Webhook],
            expectedStatus: "unauthorized");

        await AssertHealthChannelStatusAsync(
            allowedChannels: [AlertChannelType.Webhook],
            configuredChannels: [],
            expectedStatus: "unconfigured");
    }

    private async Task<long> CreateZoneAsync(string serviceId)
    {
        var payload = new
        {
            serviceId,
            zoneName = "Test Zone",
            wkt = "POLYGON((-157.88 21.29,-157.88 21.31,-157.85 21.31,-157.85 21.29,-157.88 21.29))",
            srid = 4326,
            isActive = true
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/alerts/zones", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("zoneId").GetInt64();
    }

    private static WebAppFixture CreateFixture(
        IReadOnlyCollection<AlertChannelType> allowedChannels,
        IReadOnlyCollection<AlertChannelType> configuredChannels)
    {
        return new WebAppFixture().ReplaceService<IAlertEditionPolicy>(
            new TestAlertEditionPolicy(allowedChannels, configuredChannels));
    }

    private static WebAppFixture CreateStubbedFixture(
        IAlertAdminStore store,
        IAuditLog auditLog,
        IAlertEventQuery eventQuery,
        IReadOnlyCollection<AlertChannelType>? allowedChannels = null,
        IReadOnlyCollection<AlertChannelType>? configuredChannels = null)
    {
        return new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new NoopMigrationRunner());

                services.RemoveAll<IAlertAdminStore>();
                services.RemoveAll<IAuditLog>();
                services.RemoveAll<IAlertEventQuery>();
                services.AddSingleton(store);
                services.AddSingleton(auditLog);
                services.AddSingleton(eventQuery);
            })
            .ReplaceService<IAlertEditionPolicy>(
                new TestAlertEditionPolicy(
                    allowedChannels ?? [AlertChannelType.Webhook],
                    configuredChannels ?? [AlertChannelType.Webhook]));
    }

    private static async Task AssertHealthChannelStatusAsync(
        IReadOnlyCollection<AlertChannelType> allowedChannels,
        IReadOnlyCollection<AlertChannelType> configuredChannels,
        string expectedStatus)
    {
        var store = new StubAlertAdminStore();
        store.Rules[43] = new AlertRuleDefinition
        {
            RuleId = 43,
            ServiceId = "svc",
            LayerId = 1,
            RuleName = "Webhook Rule",
            TriggerType = AlertTriggerType.Threshold,
            ConditionsJson = "{\"field\":\"speedKmh\",\"operator\":\">\",\"value\":30}",
            CooldownSeconds = 30,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray.Create(AlertChannelType.Webhook),
            IsActive = true
        };
        store.Health = new AlertRuleHealthSnapshot
        {
            RuleId = 43,
            ActiveIncidentCount = 0,
            RecentTriggerCount = 0,
            CoolingDownFeatureCount = 0,
            DeliveryFailureCount = 0,
            DeadLetterCount = 0,
            DeliveryChannels = ImmutableArray.Create(new AlertRuleDeliveryHealth
            {
                ChannelType = AlertChannelType.Webhook,
                PendingCount = 0,
                ProcessingCount = 0,
                DeliveredCount = 0,
                FailedCount = 0,
                DeadLetterCount = 0
            })
        };

        var fixture = CreateStubbedFixture(
            store,
            new CapturingAuditLog(),
            new StubAlertEventQuery(),
            allowedChannels,
            configuredChannels);

        try
        {
            await fixture.InitializeAsync();
            using var client = fixture.CreateAdminClient();

            var response = await client.GetAsync("/api/v1/admin/alerts/rules/43/health");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement
                .GetProperty("data")
                .GetProperty("deliveryChannels")[0]
                .GetProperty("status")
                .GetString()
                .Should()
                .Be(expectedStatus);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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

    private sealed class StubAlertAdminStore : IAlertAdminStore
    {
        private long _nextRuleId = 100;

        public Dictionary<long, AlertRuleDefinition> Rules { get; } = new();

        public AlertRuleHealthSnapshot? Health { get; set; }

        public Task<IReadOnlyList<AlertZoneDefinition>> ListZonesAsync(string? serviceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AlertZoneDefinition>>(Array.Empty<AlertZoneDefinition>());

        public Task<AlertZoneDefinition?> GetZoneAsync(long zoneId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertZoneDefinition?>(null);

        public Task<AlertZoneDefinition> CreateZoneAsync(AlertZoneDefinition zone, CancellationToken cancellationToken = default)
            => Task.FromResult(zone with { ZoneId = zone.ZoneId == 0 ? 1 : zone.ZoneId });

        public Task<AlertZoneDefinition?> UpdateZoneAsync(AlertZoneDefinition zone, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertZoneDefinition?>(zone);

        public Task<bool> DeleteZoneAsync(long zoneId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<AlertRuleDefinition>> ListRulesAsync(string? serviceId, int? layerId, CancellationToken cancellationToken = default)
        {
            var results = Rules.Values
                .Where(rule => string.IsNullOrWhiteSpace(serviceId) || string.Equals(rule.ServiceId, serviceId, StringComparison.Ordinal))
                .Where(rule => !layerId.HasValue || rule.LayerId == layerId.Value)
                .ToArray();

            return Task.FromResult<IReadOnlyList<AlertRuleDefinition>>(results);
        }

        public Task<AlertRuleDefinition?> GetRuleAsync(long ruleId, CancellationToken cancellationToken = default)
        {
            Rules.TryGetValue(ruleId, out var rule);
            return Task.FromResult(rule);
        }

        public Task<AlertRuleDefinition> CreateRuleAsync(AlertRuleDefinition rule, CancellationToken cancellationToken = default)
        {
            var created = rule with { RuleId = _nextRuleId++ };
            Rules[created.RuleId] = created;
            return Task.FromResult(created);
        }

        public Task<AlertRuleDefinition?> UpdateRuleAsync(AlertRuleDefinition rule, CancellationToken cancellationToken = default)
        {
            if (!Rules.ContainsKey(rule.RuleId))
            {
                return Task.FromResult<AlertRuleDefinition?>(null);
            }

            Rules[rule.RuleId] = rule;
            return Task.FromResult<AlertRuleDefinition?>(rule);
        }

        public Task<bool> DeleteRuleAsync(long ruleId, CancellationToken cancellationToken = default)
            => Task.FromResult(Rules.Remove(ruleId));

        public Task<AlertRuleHealthSnapshot?> GetRuleHealthAsync(long ruleId, int recentTriggerLimit, CancellationToken cancellationToken = default)
            => Task.FromResult(Health);
    }

    private sealed class StubAlertEventQuery : IAlertEventQuery
    {
        public AlertEventPage Page { get; set; } = new() { Items = Array.Empty<AlertEventSummary>() };

        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(Page);

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertEventSummary?>(Page.Items.FirstOrDefault(item => item.EventId == eventId));
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Recorded { get; } = new();

        public Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(auditEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopMigrationRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(string connectionString,
            System.Reflection.Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(string connectionString,
            System.Reflection.Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}

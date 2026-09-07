// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertPreviewFloorTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = CreateFixture();
    private HttpClient _client = null!;
    private readonly string _service = $"alert-floor-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}/enabled")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task Lifecycle_AllEightActions_PersistIdentityAndMutationTogether()
    {
        var start = DateTimeOffset.UtcNow;
        var correlation = Guid.NewGuid().ToString("N");
        _client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        var zoneId = await ReadIdAsync(await _client.PostAsJsonAsync("/api/v1/admin/alerts/zones", ZonePayload("original")), "zoneId");
        (await _client.PutAsJsonAsync($"/api/v1/admin/alerts/zones/{zoneId}", ZonePayload("updated"))).StatusCode.Should().Be(HttpStatusCode.OK);
        var storedZone = await _fixture.GetService<IAlertAdminStore>().GetZoneAsync(zoneId);
        storedZone!.ZoneName.Should().Be("updated");
        storedZone.GeometrySrid.Should().Be(4326);
        var geometry = new NetTopologySuite.IO.WKBReader().Read(storedZone.Geometry!);
        geometry.Area.Should().Be(4);
        geometry.Coordinates.Select(point => (point.X, point.Y)).Should().Equal((0d, 0d), (0d, 2d), (2d, 2d), (2d, 0d), (0d, 0d));
        var ruleId = await ReadIdAsync(await _client.PostAsJsonAsync("/api/v1/admin/alerts/rules", RulePayload("original", zoneId)), "ruleId");
        (await _client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{ruleId}", RulePayload("updated", zoneId))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{ruleId}/enabled", new { enabled = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fixture.GetService<IAlertAdminStore>().GetRuleAsync(ruleId))!.IsActive.Should().BeFalse();
        (await _client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{ruleId}/enabled", new { enabled = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        var storedRule = await _fixture.GetService<IAlertAdminStore>().GetRuleAsync(ruleId);
        storedRule!.RuleName.Should().Be("updated");
        storedRule.IsActive.Should().BeTrue();
        (await _client.DeleteAsync($"/api/v1/admin/alerts/rules/{ruleId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.DeleteAsync($"/api/v1/admin/alerts/zones/{zoneId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _fixture.GetService<IAlertAdminStore>().GetRuleAsync(ruleId)).Should().BeNull();
        (await _fixture.GetService<IAlertAdminStore>().GetZoneAsync(zoneId)).Should().BeNull();

        await using var connection = await _fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT action, actor, actor_type, event_type, outcome, timestamp, correlation_id, details, resource_type, resource_id
            FROM honua.audit_log
            WHERE (resource_type = 'alert_zone' AND resource_id = @zone)
               OR (resource_type = 'alert_rule' AND resource_id = @rule)
            ORDER BY audit_id
            """, connection);
        command.Parameters.AddWithValue("zone", zoneId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("rule", ruleId.ToString(CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync();
        var actions = new List<string>();
        while (await reader.ReadAsync())
        {
            var action = reader.GetString(0);
            actions.Add(action);
            // The authenticated fixture key is seeded with this fixed principal ID.
            reader.GetString(1).Should().Be(WebAppFixture.SharedAdminActorId);
            reader.GetString(2).Should().Be("ApiKey");
            reader.GetString(3).Should().Be("ConfigChange");
            reader.GetString(4).Should().Be("Success");
            new DateTimeOffset(reader.GetDateTime(5)).Should().BeOnOrAfter(start).And.BeOnOrBefore(DateTimeOffset.UtcNow);
            reader.GetString(6).Should().Be(correlation);
            var isZone = action.StartsWith("alert_zone.", StringComparison.Ordinal);
            reader.GetString(8).Should().Be(isZone ? "alert_zone" : "alert_rule");
            reader.GetString(9).Should().Be((isZone ? zoneId : ruleId).ToString(CultureInfo.InvariantCulture));
            using var details = JsonDocument.Parse(reader.GetString(7));
            details.RootElement.GetProperty(isZone ? "zoneId" : "ruleId").GetInt64().Should().Be(isZone ? zoneId : ruleId);
            if (action != "alert_zone.delete")
            {
                details.RootElement.GetProperty("serviceId").GetString().Should().Be(_service);
                details.RootElement.GetProperty("enabled").GetBoolean().Should().Be(action != "alert_rule.disable");
            }
            if (!isZone)
            {
                details.RootElement.GetProperty("layerId").GetInt32().Should().Be(1);
                details.RootElement.GetProperty("zoneId").GetInt64().Should().Be(zoneId);
            }
        }
        actions.Should().Equal("alert_zone.create", "alert_zone.update", "alert_rule.create", "alert_rule.update",
            "alert_rule.disable", "alert_rule.enable", "alert_rule.delete", "alert_zone.delete");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/rules")]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    public async Task Lists_MissingScopeRejects_ExplicitScopeExcludesAnotherService()
    {
        var store = _fixture.GetService<IAlertAdminStore>();
        var own = await store.CreateRuleAsync(Rule(_service));
        var other = await store.CreateRuleAsync(Rule(_service + "-other"));
        foreach (var suffix in new[] { "", "?serviceId=", "?serviceId=%20", "?layerId=1" })
        {
            (await _client.GetAsync("/api/v1/admin/alerts/rules" + suffix)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await _client.GetAsync("/api/v1/admin/alerts/zones" + suffix)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        var response = await _client.GetAsync($"/api/v1/admin/alerts/rules?serviceId={_service}&layerId=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").EnumerateArray().Select(row => row.GetProperty("ruleId").GetInt64())
            .Should().Equal(own.RuleId).And.NotContain(other.RuleId);
    }

    [IntegrationTheory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task Requests_AnonymousAndTenantScopedAdmin_DenyWithoutDisclosureOrMutation(string tenantId)
    {
        var rule = await _fixture.GetService<IAlertAdminStore>().CreateRuleAsync(Rule(_service));
        using var anonymous = _fixture.CreateClient();
        foreach (var client in new[] { anonymous, _client })
        {
            if (client == _client)
            {
                client.DefaultRequestHeaders.Add("X-Honua-Tenant", tenantId);
            }
            var expected = client == anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            foreach (var path in new[] { $"/api/v1/admin/alerts/rules/{rule.RuleId}", $"/api/v1/admin/alerts/rules?serviceId={_service}", "/api/v1/admin/alerts/channels", "/api/v1/admin/observability/alerts" })
            {
                var response = await client.GetAsync(path);
                response.StatusCode.Should().Be(expected);
                (await response.Content.ReadAsStringAsync()).Should().NotContain(_service);
            }
            (await client.DeleteAsync($"/api/v1/admin/alerts/rules/{rule.RuleId}")).StatusCode.Should().Be(expected);
            (await _fixture.GetService<IAlertAdminStore>().GetRuleAsync(rule.RuleId))!.RuleName.Should().Be("original");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task Mutations_AuditReturnsNoIdentity_RollBackRealPostgresWrites()
    {
        var audit = Substitute.For<IAuditLog>();
        audit.IsPersisted.Returns(true);
        audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));
        await using var fixture = CreateFixture().ReplaceService(audit);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var store = fixture.GetService<IAlertAdminStore>();
        var existing = await store.CreateRuleAsync(Rule(_service));
        var zone = await store.CreateZoneAsync(new AlertZoneDefinition
        {
            ZoneId = 0,
            ServiceId = _service,
            ZoneName = "original",
            IsActive = true,
            GeometrySrid = 4326,
            Geometry = new NetTopologySuite.IO.WKTReader().Read("MULTIPOLYGON(((0 0,0 2,2 2,2 0,0 0)))").AsBinary()
        });
        foreach (var createRequest in new Func<HttpRequestMessage>[]
        {
            () => new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/alerts/rules") { Content = JsonContent.Create(RulePayload("new", null)) },
            () => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/rules/{existing.RuleId}") { Content = JsonContent.Create(RulePayload("changed", null)) },
            () => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/rules/{existing.RuleId}/enabled") { Content = JsonContent.Create(new { enabled = false }) },
            () => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/alerts/rules/{existing.RuleId}"),
            () => new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/alerts/zones") { Content = JsonContent.Create(ZonePayload("new")) },
            () => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/zones/{zone.ZoneId}") { Content = JsonContent.Create(ZonePayload("changed")) },
            () => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/alerts/zones/{zone.ZoneId}")
        })
        {
            using var request = createRequest();
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var stored = await store.GetRuleAsync(existing.RuleId);
            stored!.RuleName.Should().Be("original");
            stored.IsActive.Should().BeTrue();
            var storedZone = await store.GetZoneAsync(zone.ZoneId);
            storedZone!.ZoneName.Should().Be("original");
            storedZone.Geometry.Should().Equal(zone.Geometry!);
            storedZone.GeometrySrid.Should().Be(4326);
            (await store.ListZonesAsync(_service)).Select(row => row.ZoneId).Should().Equal(zone.ZoneId);
            (await store.ListRulesAsync(_service, 1)).Select(row => row.RuleId).Should().Equal(existing.RuleId);
        }
    }

    [IntegrationTheory]
    [InlineData("alert_zone.create")]
    [InlineData("alert_zone.update")]
    [InlineData("alert_zone.delete")]
    [InlineData("alert_rule.create")]
    [InlineData("alert_rule.update")]
    [InlineData("alert_rule.enable")]
    [InlineData("alert_rule.disable")]
    [InlineData("alert_rule.delete")]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}/enabled")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public Task Mutations_PostgresRejectsAuditInsert_LeaveDataAndAuditUnchanged(string action) =>
        VerifyAuditRollbackAsync(action, _fixture, _client);

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task Review_ConnectionVariants_CommitAndRollbackWithDurableAudit(bool namedConnection)
    {
        await using var fixture = CreateFixture().ConfigureServices(services =>
        {
            services.AddSingleton(provider => new ReviewConnectionResources(
                provider.GetRequiredService<NpgsqlDataSource>(), namedConnection));
            services.RemoveAll<IAdoNetDatabaseConnectionProvider>();
            services.AddScoped<IAdoNetDatabaseConnectionProvider>(provider =>
                provider.GetRequiredService<ReviewConnectionResources>().CreateProvider());
        });
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        await VerifyAuditRollbackAsync("alert_rule.update", fixture, client);

        var store = fixture.GetService<IAlertAdminStore>();
        var rule = (await store.ListRulesAsync(_service, 1)).Single();
        using var response = await client.PutAsJsonAsync($"/api/v1/admin/alerts/rules/{rule.RuleId}", RulePayload("committed", null));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await store.GetRuleAsync(rule.RuleId))!.RuleName.Should().Be("committed");
        await using var connection = await fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM honua.audit_log
            WHERE resource_type = 'alert_rule' AND resource_id = @rule AND action = 'alert_rule.update'
                AND actor = @actor AND actor_type = 'ApiKey' AND outcome = 'Success'
            """, connection);
        command.Parameters.AddWithValue("rule", rule.RuleId.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("actor", WebAppFixture.SharedAdminActorId);
        (await command.ExecuteScalarAsync()).Should().Be(1L);
        var resources = fixture.GetService<ReviewConnectionResources>();
        resources.ObservedAmbientTransaction.Should().BeFalse();
        if (namedConnection)
        {
            resources.RegistryLookups.Should().BeGreaterThan(0);
        }
        else
        {
            resources.RegistryLookups.Should().Be(0);
        }
    }

    [IntegrationTheory]
    [InlineData("/api/v1/admin/alerts/rules/test")]
    [InlineData("/api/v1/admin/alerts/rules/test/")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    public async Task Review_RuleTestPathVariants_DoNotRequireAuditOrPersistRules(string path)
    {
        var audit = Substitute.For<IAuditLog>();
        audit.IsPersisted.Returns(false);
        await using var fixture = CreateFixture().ReplaceService(audit);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.PostAsJsonAsync(path, new { rule = RulePayload("draft", null) });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await fixture.GetService<IAlertAdminStore>().ListRulesAsync(_service, 1)).Should().BeEmpty();
        await audit.DidNotReceive().RecordAsync(
            Arg.Is<AuditEvent>(row => row.ResourceType == "alert_rule" || row.ResourceType == "alert_zone"),
            Arg.Any<CancellationToken>());
    }

    private async Task VerifyAuditRollbackAsync(string action, WebAppFixture fixture, HttpClient client)
    {
        var store = fixture.GetService<IAlertAdminStore>();
        var rule = await store.CreateRuleAsync(Rule(_service) with { IsActive = action != "alert_rule.enable" });
        var zone = await store.CreateZoneAsync(new AlertZoneDefinition
        {
            ZoneId = 0,
            ServiceId = _service,
            ZoneName = "original",
            IsActive = true,
            GeometrySrid = 4326,
            Geometry = new NetTopologySuite.IO.WKTReader().Read("MULTIPOLYGON(((0 0,0 2,2 2,2 0,0 0)))").AsBinary()
        });
        var correlation = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        // Reject only this request's INSERT in the real migrated audit table.
        // No fake sink or mutation-response echo supplies the persistence oracle.
        var trigger = "alert_floor_" + correlation;
        await using var connection = await fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var install = new NpgsqlCommand($"""
            CREATE FUNCTION honua.{trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.correlation_id = '{correlation}' THEN
                    RAISE EXCEPTION 'injected audit persistence failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER {trigger} BEFORE INSERT ON honua.audit_log
                FOR EACH ROW EXECUTE FUNCTION honua.{trigger}();
            """, connection);
        await install.ExecuteNonQueryAsync();
        try
        {
            using var request = action switch
            {
                "alert_zone.create" => new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/alerts/zones") { Content = JsonContent.Create(ZonePayload("new")) },
                "alert_zone.update" => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/zones/{zone.ZoneId}") { Content = JsonContent.Create(ZonePayload("changed")) },
                "alert_zone.delete" => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/alerts/zones/{zone.ZoneId}"),
                "alert_rule.create" => new HttpRequestMessage(HttpMethod.Post, "/api/v1/admin/alerts/rules") { Content = JsonContent.Create(RulePayload("new", null)) },
                "alert_rule.update" => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/rules/{rule.RuleId}") { Content = JsonContent.Create(RulePayload("changed", null)) },
                "alert_rule.delete" => new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/admin/alerts/rules/{rule.RuleId}"),
                _ => new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/alerts/rules/{rule.RuleId}/enabled") { Content = JsonContent.Create(new { enabled = action == "alert_rule.enable" }) }
            };
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, await response.Content.ReadAsStringAsync());
            var persisted = await store.GetRuleAsync(rule.RuleId);
            persisted!.RuleName.Should().Be("original");
            persisted.IsActive.Should().Be(action != "alert_rule.enable");
            var persistedZone = await store.GetZoneAsync(zone.ZoneId);
            persistedZone!.ZoneName.Should().Be("original");
            persistedZone.Geometry.Should().Equal(zone.Geometry!);
            persistedZone.GeometrySrid.Should().Be(4326);
            (await store.ListRulesAsync(_service, 1)).Select(row => row.RuleId).Should().Equal(rule.RuleId);
            (await store.ListZonesAsync(_service)).Select(row => row.ZoneId).Should().Equal(zone.ZoneId);
            await using var count = new NpgsqlCommand("SELECT count(*) FROM honua.audit_log WHERE correlation_id = @correlation", connection);
            count.Parameters.AddWithValue("correlation", correlation);
            (await count.ExecuteScalarAsync()).Should().Be(0L);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"DROP TRIGGER {trigger} ON honua.audit_log; DROP FUNCTION honua.{trigger}();", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class ReviewConnectionResources : ISecureConnectionResolver, IDisposable
    {
        private readonly NpgsqlDataSource _primary;
        private readonly NpgsqlDataSource _target;
        private readonly bool _named;
        private readonly IConfiguration _configuration;
        private readonly SecureConnectionDataSourceCache _cache;
        internal bool ObservedAmbientTransaction { get; private set; }
        internal int RegistryLookups { get; private set; }

        internal ReviewConnectionResources(NpgsqlDataSource primary, bool named)
        {
            _primary = primary;
            _named = named;
            _target = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(primary.ConnectionString)
            {
                Enlist = named,
                ApplicationName = "alert-review-target"
            }.ConnectionString);
            _configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:SecureConnection:Name"] = "alert-review-target"
            }).Build();
            _cache = new SecureConnectionDataSourceCache(_configuration);
        }

        internal IAdoNetDatabaseConnectionProvider CreateProvider() => _named
            ? new SecureConnectionAwareDatabaseProvider(new TestDatabaseConnectionProvider(_primary), this,
                _cache, _configuration, NullLogger<SecureConnectionAwareDatabaseProvider>.Instance)
            : new TestDatabaseConnectionProvider(_target);

        public async Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
        {
            RegistryLookups++;
            if (System.Transactions.Transaction.Current is not null)
            {
                ObservedAmbientTransaction = true;
            }
            // Execute a real registry-database lookup using a distinct data source.
            await using var connection = await _primary.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(1);
            return _target.ConnectionString;
        }

        public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
        public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public void Dispose()
        {
            _cache.Dispose();
            _target.Dispose();
        }
    }

    private object ZonePayload(string name) => new
    {
        serviceId = _service,
        zoneName = name,
        wkt = "POLYGON((0 0,0 2,2 2,2 0,0 0))",
        srid = 4326,
        isActive = true
    };

    private object RulePayload(string name, long? zoneId) => new
    {
        serviceId = _service,
        layerId = 1,
        zoneId,
        ruleName = name,
        triggerType = zoneId.HasValue ? "enter" : "threshold",
        conditionsJson = "{\"field\":\"speed\",\"operator\":\">\",\"value\":30}",
        cooldownSeconds = 60,
        severity = "warning",
        editionRequired = "pro",
        channels = new[] { "webhook" },
        isActive = true
    };

    private static AlertRuleDefinition Rule(string service) => new()
    {
        RuleId = 0,
        ServiceId = service,
        LayerId = 1,
        RuleName = "original",
        TriggerType = AlertTriggerType.Threshold,
        ConditionsJson = "{\"field\":\"speed\",\"operator\":\">\",\"value\":30}",
        CooldownSeconds = 60,
        Severity = AlertSeverity.Warning,
        EditionRequired = AlertEdition.Pro,
        Channels = ImmutableArray.Create(AlertChannelType.Webhook),
        IsActive = true
    };

    private static async Task<long> ReadIdAsync(HttpResponseMessage response, string property)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty(property).GetInt64();
    }

    private static WebAppFixture CreateFixture()
    {
        var policy = Substitute.For<IAlertEditionPolicy>();
        policy.IsRuleAllowed(Arg.Any<AlertRuleDefinition>()).Returns(true);
        policy.IsTriggerAllowed(Arg.Any<AlertTriggerType>()).Returns(true);
        policy.IsChannelAllowed(Arg.Any<AlertChannelType>()).Returns(true);
        policy.IsChannelConfigured(Arg.Any<AlertChannelType>()).Returns(true);
        return new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise)
            .ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false").UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false").UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword))
            .ReplaceService(policy);
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// honua-io/honua-server#3859, fail-closed leg: alert rules, state, events, dispatch,
/// lifecycle, checkpoint and channel-state rows carry no tenant column, so 2026.1 ships
/// Preview alerting on single-tenant instances only. Two mechanisms enforce that:
/// <see cref="Honua.Alerts.AlertsServiceCollectionExtensions"/> refuses to start the
/// evaluation and delivery workers when tenant resolution or schema routing is enabled,
/// and <c>AlertAdminIsolationFilter</c> refuses every tenant-scoped HTTP caller.
///
/// <para>
/// <c>AlertPreviewFloorTests.Requests_AnonymousAndTenantScopedAdmin_DenyWithoutDisclosureOrMutation</c>
/// already proves the refusal on four hand-picked paths. The gap this class closes is
/// <b>exhaustiveness</b>: nothing proved that the filter is wired onto <i>every</i> deployed
/// alert route. <c>AddEndpointFilter</c> leaves no endpoint metadata, so a new alert route
/// mapped without it is invisible to review and to every existing test, and would hand a
/// tenant-scoped administrator instance-wide alert data on a multi-tenant deployment — the
/// exact confidentiality failure #3859 names.
/// </para>
///
/// <para>
/// The denominator here is therefore the live <see cref="EndpointDataSource"/> of the running
/// server, not a hand-maintained list: every registered route under the alert admin surfaces is
/// driven, and an unrecognised route parameter fails the run rather than being skipped. Adding
/// an unguarded alert route breaks this test on the PR that adds it.
/// </para>
///
/// <para>
/// The claim-sourced arm of the filter is covered by
/// <c>AlertAdminIsolationFilterTests</c>; the fixture's API-key principal carries no tenant
/// claim, so the header/resolved-tenant arm is what is exercised at the wire here.
/// </para>
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertTenantIsolationRouteCoverageProofTests : IAsyncLifetime
{
    /// <summary>The other tenant's identifier. Must never appear in any response body.</summary>
    private const string ForeignTenantId = "tenant-b-3859";

    /// <summary>
    /// Route prefixes whose handlers read or mutate the instance-wide alert stores
    /// (<c>IAlertAdminStore</c>, <c>IAlertEventQuery</c>, <c>IAlertLifecycleStore</c>,
    /// <c>IAlertDispatchStore</c>). Every deployed route under these prefixes must fail closed.
    /// </summary>
    private static readonly string[] AlertAdminRoutePrefixes =
    [
        "/api/v{version:apiVersion}/admin/alerts",
        "/api/v{version:apiVersion}/admin/observability/alerts"
    ];

    /// <summary>
    /// The alert routes deployed when this proof was written. Discovery drives the assertions,
    /// so a <i>new</i> route is covered automatically; this list additionally fails the test if a
    /// route is <i>removed or renamed</i>, which would otherwise silently shrink the denominator.
    /// </summary>
    private static readonly string[] KnownAlertRoutes =
    [
        "GET /api/v{version:apiVersion}/admin/alerts/zones",
        "POST /api/v{version:apiVersion}/admin/alerts/zones",
        "GET /api/v{version:apiVersion}/admin/alerts/zones/{zoneId:long}",
        "PUT /api/v{version:apiVersion}/admin/alerts/zones/{zoneId:long}",
        "DELETE /api/v{version:apiVersion}/admin/alerts/zones/{zoneId:long}",
        "GET /api/v{version:apiVersion}/admin/alerts/rules",
        "POST /api/v{version:apiVersion}/admin/alerts/rules",
        "POST /api/v{version:apiVersion}/admin/alerts/rules/test",
        "GET /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}",
        "PUT /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}",
        "DELETE /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}",
        "PUT /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}/enabled",
        "GET /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}/health",
        "GET /api/v{version:apiVersion}/admin/alerts/rules/{ruleId:long}/events",
        "POST /api/v{version:apiVersion}/admin/alerts/dispatch/redrive",
        "GET /api/v{version:apiVersion}/admin/alerts/channels",
        "POST /api/v{version:apiVersion}/admin/alerts/channels/{channel}/pause",
        "POST /api/v{version:apiVersion}/admin/alerts/channels/{channel}/resume",
        "GET /api/v{version:apiVersion}/admin/observability/alerts/",
        "GET /api/v{version:apiVersion}/admin/observability/alerts/{eventId:long}",
        "POST /api/v{version:apiVersion}/admin/observability/alerts/{eventId:long}/acknowledge",
        "POST /api/v{version:apiVersion}/admin/observability/alerts/{eventId:long}/suppress",
        "POST /api/v{version:apiVersion}/admin/observability/alerts/{eventId:long}/resolve"
    ];

    private static readonly Regex RouteParameterPattern = new(@"\{(?<name>[^:}]+)(?::[^}]+)?\}", RegexOptions.Compiled);

    private readonly WebAppFixture _fixture = CreateFixture();
    private readonly string _service = $"alert-3859-{Guid.NewGuid():N}";
    private HttpClient _instanceAdmin = null!;
    private HttpClient _tenantScopedAdmin = null!;
    private long _zoneId;
    private long _ruleId;
    private long _eventId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _instanceAdmin = _fixture.CreateAdminClient();
        _tenantScopedAdmin = _fixture.CreateAdminClient();
        // The multi-tenant admin header is the wire form of "this caller is acting for a tenant".
        _tenantScopedAdmin.DefaultRequestHeaders.Add(TenantContextOptions.TenantHeaderName, ForeignTenantId);

        var store = _fixture.GetService<IAlertAdminStore>();
        var zone = await store.CreateZoneAsync(new AlertZoneDefinition
        {
            ZoneId = 0,
            ServiceId = _service,
            ZoneName = "original",
            IsActive = true,
            GeometrySrid = 4326,
            Geometry = new NetTopologySuite.IO.WKTReader().Read("MULTIPOLYGON(((0 0,0 2,2 2,2 0,0 0)))").AsBinary()
        });
        _zoneId = zone.ZoneId;
        _ruleId = (await store.CreateRuleAsync(new AlertRuleDefinition
        {
            RuleId = 0,
            ServiceId = _service,
            LayerId = 1,
            RuleName = "original",
            TriggerType = AlertTriggerType.Threshold,
            ConditionsJson = "{\"field\":\"speed\",\"operator\":\">\",\"value\":30}",
            CooldownSeconds = 60,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray.Create(AlertChannelType.Webhook),
            IsActive = true
        })).RuleId;
        _eventId = (await _fixture.GetService<IAlertEventStore>().TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"{_service}-3859",
            RuleId = _ruleId,
            ZoneId = _zoneId,
            ServiceId = _service,
            LayerId = 1,
            ObjectId = 42,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 1,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow
        }))!.Value;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Drives every deployed alert admin route twice: once as a tenant-scoped administrator
    /// (must be refused) and once as the instance administrator (must not be refused). The
    /// second arm is what makes the first non-vacuous — without it a blanket 403 from
    /// authorization, routing, or the capability gate would satisfy the refusal assertion
    /// while proving nothing about tenant isolation.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    [Endpoint("GET /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("GET /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}/enabled")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/health")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/events")]
    [Endpoint("POST /api/v1/admin/alerts/dispatch/redrive")]
    [Endpoint("GET /api/v1/admin/alerts/channels")]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/pause")]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/resume")]
    [Endpoint("GET /api/v1/admin/observability/alerts")]
    [Endpoint("GET /api/v1/admin/observability/alerts/{eventId}")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/acknowledge")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/resolve")]
    public async Task EveryDeployedAlertRoute_RefusesTenantScopedAdmin_AndServesInstanceAdmin()
    {
        var deployed = DiscoverAlertRoutes();

        deployed.Keys.Should().Contain(KnownAlertRoutes,
            "an alert route that disappeared from the deployed surface silently shrinks the isolation denominator");

        foreach (var (route, request) in deployed)
        {
            using var refusedRequest = request();
            using var refused = await _tenantScopedAdmin.SendAsync(refusedRequest);
            var refusedBody = await refused.Content.ReadAsStringAsync();

            refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "{0} must fail closed for a tenant-scoped caller; alert rows carry no tenant ownership. Body: {1}",
                route, refusedBody);
            refusedBody.Should().Contain("Preview alert administration requires an instance administrator",
                "{0} must be refused by AlertAdminIsolationFilter, not by an unrelated denial", route);
            refusedBody.Should().NotContain(ForeignTenantId,
                "{0} must not echo the tenant identifier back to the caller", route);
            refusedBody.Should().NotContain(_service,
                "{0} must not disclose instance-wide alert data in its refusal", route);

            using var servedRequest = request();
            using var served = await _instanceAdmin.SendAsync(servedRequest);
            served.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
                "{0} must be reachable by an instance administrator, otherwise the tenant-scoped 403 proves nothing " +
                "about tenant scope. Body: {1}", route, await served.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// The refusal must also leave no trace: no alert row changes and no audit record is
    /// written for the tenant-scoped attempts. #3859 requires that a cross-tenant list, get,
    /// acknowledge, resolve, suppress or redrive "fail closed and create no mutation".
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/zones")]
    [Endpoint("POST /api/v1/admin/alerts/zones")]
    [Endpoint("GET /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("PUT /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/zones/{zoneId}")]
    [Endpoint("GET /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules")]
    [Endpoint("POST /api/v1/admin/alerts/rules/test")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    [Endpoint("PUT /api/v1/admin/alerts/rules/{ruleId}/enabled")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/health")]
    [Endpoint("GET /api/v1/admin/alerts/rules/{ruleId}/events")]
    [Endpoint("POST /api/v1/admin/alerts/dispatch/redrive")]
    [Endpoint("GET /api/v1/admin/alerts/channels")]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/pause")]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/resume")]
    [Endpoint("GET /api/v1/admin/observability/alerts")]
    [Endpoint("GET /api/v1/admin/observability/alerts/{eventId}")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/acknowledge")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/resolve")]
    public async Task TenantScopedAttempts_OnEveryDeployedAlertRoute_MutateNothingAndAuditNothing()
    {
        var store = _fixture.GetService<IAlertAdminStore>();
        var zoneBefore = await store.GetZoneAsync(_zoneId);
        var ruleBefore = await store.GetRuleAsync(_ruleId);
        var eventBefore = await _fixture.GetService<IAlertEventStore>().GetAsync(_eventId);
        var lifecycleBefore = await _fixture.GetService<IAlertLifecycleStore>().GetAsync(_eventId);

        var correlation = Guid.NewGuid().ToString("N");
        _tenantScopedAdmin.DefaultRequestHeaders.Add("X-Correlation-ID", correlation);
        var auditRowsBefore = await CountAlertAuditRowsAsync();

        foreach (var (route, request) in DiscoverAlertRoutes())
        {
            using var message = request();
            using var response = await _tenantScopedAdmin.SendAsync(message);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, route);
        }

        (await store.GetZoneAsync(_zoneId)).Should().BeEquivalentTo(zoneBefore);
        (await store.GetRuleAsync(_ruleId)).Should().BeEquivalentTo(ruleBefore);
        (await _fixture.GetService<IAlertEventStore>().GetAsync(_eventId)).Should().BeEquivalentTo(eventBefore);
        (await _fixture.GetService<IAlertLifecycleStore>().GetAsync(_eventId)).Should().BeEquivalentTo(lifecycleBefore);
        (await store.ListZonesAsync(_service)).Select(row => row.ZoneId).Should().Equal(_zoneId);
        (await store.ListRulesAsync(_service, 1)).Select(row => row.RuleId).Should().Equal(_ruleId);

        // The alert domain must record nothing: no alert zone, rule, channel or event audit
        // action may appear, because no handler ran. The zone/rule endpoints propagate
        // X-Correlation-ID while the ops channel endpoints stamp HttpContext.TraceIdentifier,
        // so the resource-type count is checked independently of the correlation view.
        (await CountAlertAuditRowsAsync()).Should().Be(auditRowsBefore,
            "a refused tenant-scoped request must not record an alert-domain audit action");

        // Denied admin requests ARE audited, by design: the access-audit trail is how an
        // instance operator sees a rejected tenant-scoped attempt. What those rows must never
        // do is carry an alert-domain resource, report success, or disclose instance alert data.
        var audited = await ReadSweepAuditRowsAsync(correlation);
        audited.Should().NotBeEmpty("a refused tenant-scoped alert request is a security event and must be audited");
        audited.Should().OnlyContain(row => row.Outcome != "Success",
            "no refused request may be audited as a successful action");
        audited.Should().NotContain(row => AlertAuditResourceTypes.Contains(row.ResourceType),
            "a refused request reached no alert handler, so no alert-domain audit row may exist");
        audited.Should().OnlyContain(row => !row.Details.Contains(_service, StringComparison.Ordinal),
            "the audit trail of a refused request must not disclose the instance's alert service identifiers");
    }

    private static readonly string[] AlertAuditResourceTypes =
        ["alert_zone", "alert_rule", "alert-channel", "alert_event"];

    /// <summary>Counts every audit row attached to an alert zone, rule, delivery channel, or event.</summary>
    private async Task<object?> CountAlertAuditRowsAsync()
    {
        await using var connection = await _fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM honua.audit_log WHERE resource_type = ANY(@types)", connection);
        command.Parameters.AddWithValue("types", AlertAuditResourceTypes);
        return await command.ExecuteScalarAsync();
    }

    /// <summary>Reads the audit rows the refused sweep produced, for outcome and disclosure checks.</summary>
    private async Task<IReadOnlyList<(string ResourceType, string Outcome, string Details)>> ReadSweepAuditRowsAsync(
        string correlation)
    {
        var rows = new List<(string, string, string)>();
        await using var connection = await _fixture.GetService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT resource_type, outcome, coalesce(details, '') FROM honua.audit_log WHERE correlation_id = @correlation",
            connection);
        command.Parameters.AddWithValue("correlation", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>
    /// Discovers every deployed alert admin route from the running server and pairs it with a
    /// concrete request. An unrecognised route parameter throws rather than being skipped, so a
    /// new alert route cannot quietly drop out of the denominator.
    /// </summary>
    private Dictionary<string, Func<HttpRequestMessage>> DiscoverAlertRoutes()
    {
        var routes = new Dictionary<string, Func<HttpRequestMessage>>(StringComparer.Ordinal);
        foreach (var source in _fixture.Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var pattern = endpoint.RoutePattern.RawText;
                if (string.IsNullOrEmpty(pattern) ||
                    !AlertAdminRoutePrefixes.Any(prefix => pattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [HttpMethods.Get])
                {
                    var key = $"{method.ToUpperInvariant()} {pattern}";
                    routes[key] = () => BuildRequest(method, pattern);
                }
            }
        }

        routes.Should().NotBeEmpty("the alert admin surface must be deployed for this proof to mean anything");
        return routes;
    }

    private HttpRequestMessage BuildRequest(string method, string pattern)
    {
        var path = RouteParameterPattern.Replace(pattern, match => match.Groups["name"].Value switch
        {
            "version" => "1",
            "zoneId" => _zoneId.ToString(CultureInfo.InvariantCulture),
            "ruleId" => _ruleId.ToString(CultureInfo.InvariantCulture),
            "eventId" => _eventId.ToString(CultureInfo.InvariantCulture),
            "channel" => "webhook",
            var unknown => throw new InvalidOperationException(
                $"Alert route '{pattern}' introduced the unmapped parameter '{{{unknown}}}'. Give it a concrete value " +
                "here so honua-server#3859's fail-closed proof keeps covering every deployed alert route.")
        });

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (HttpMethods.IsGet(method) || HttpMethods.IsDelete(method))
        {
            return request;
        }

        // Bodies only need to be well-formed: the isolation filter runs before model binding,
        // and the instance-admin control arm asserts "not 403" rather than a success code.
        request.Content = JsonContent.Create(path switch
        {
            var p when p.EndsWith("/rules/test", StringComparison.Ordinal) => new { rule = RulePayload() },
            var p when p.EndsWith("/enabled", StringComparison.Ordinal) => (object)new { enabled = true },
            var p when p.Contains("/rules", StringComparison.Ordinal) => RulePayload(),
            var p when p.Contains("/zones", StringComparison.Ordinal) => ZonePayload(),
            var p when p.EndsWith("/suppress", StringComparison.Ordinal) =>
                new { suppressUntil = DateTimeOffset.UtcNow.AddHours(1), note = "3859" },
            _ => new { note = "3859" }
        });
        return request;
    }

    private object ZonePayload() => new
    {
        serviceId = _service,
        zoneName = "attempted",
        wkt = "POLYGON((0 0,0 2,2 2,2 0,0 0))",
        srid = 4326,
        isActive = true
    };

    private object RulePayload() => new
    {
        serviceId = _service,
        layerId = 1,
        zoneId = (long?)null,
        ruleName = "attempted",
        triggerType = "threshold",
        conditionsJson = "{\"field\":\"speed\",\"operator\":\">\",\"value\":30}",
        cooldownSeconds = 60,
        severity = "warning",
        editionRequired = "pro",
        channels = new[] { "webhook" },
        isActive = true
    };

    private static WebAppFixture CreateFixture()
    {
        var policy = Substitute.For<IAlertEditionPolicy>();
        policy.IsRuleAllowed(Arg.Any<AlertRuleDefinition>()).Returns(true);
        policy.IsTriggerAllowed(Arg.Any<AlertTriggerType>()).Returns(true);
        policy.IsChannelAllowed(Arg.Any<AlertChannelType>()).Returns(true);
        policy.IsChannelConfigured(Arg.Any<AlertChannelType>()).Returns(true);
        return new WebAppFixture().WithTestLicense(HonuaEdition.Enterprise)
            .ConfigureWebHost(builder => builder
                .UseSetting("HONUA_DEV_AUTH", "false")
                .UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false")
                .UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword))
            .ReplaceService(policy);
    }
}

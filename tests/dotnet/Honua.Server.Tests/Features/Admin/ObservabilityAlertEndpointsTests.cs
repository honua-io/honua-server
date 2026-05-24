// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
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

/// <summary>
/// HTTP integration tests for the Console Operate alert endpoints (#1168). Uses
/// in-memory stubs of <see cref="IAlertEventQuery"/>, <see cref="IAlertLifecycleStore"/>,
/// and <see cref="IAuditLog"/> so wiring + DTO shape are exercised without
/// migrating the alert tables in the test database.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ObservabilityAlertEndpointsTests : IAsyncLifetime
{
    private readonly StubAlertEventQuery _query = new();
    private readonly StubAlertLifecycleStore _lifecycle = new();
    private readonly CapturingAuditLog _audit = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ObservabilityAlertEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new NoopMigrationRunner());

                services.RemoveAll<IAlertEventQuery>();
                services.RemoveAll<IAlertLifecycleStore>();
                services.RemoveAll<IAuditLog>();
                services.AddSingleton<IAlertEventQuery>(_query);
                services.AddSingleton<IAlertLifecycleStore>(_lifecycle);
                services.AddSingleton<IAuditLog>(_audit);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/alerts")]
    public async Task ListAlerts_ReturnsItemsAndResourceRef()
    {
        _query.Page = new AlertEventPage
        {
            Items = new[]
            {
                NewSummary(eventId: 7, severity: AlertSeverity.Critical, lifecycle: AlertLifecycleStatus.Open)
            }
        };

        var response = await _client.GetAsync("/api/v1/admin/observability/alerts");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("eventId").GetInt64().Should().Be(7);
        items[0].GetProperty("severity").GetString().Should().Be("critical");
        items[0].GetProperty("lifecycleStatus").GetString().Should().Be("open");
        items[0].GetProperty("resourceRef").GetString().Should().Be("alert/7");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/acknowledge")]
    public async Task AcknowledgeAlert_RecordsAuditEvidence()
    {
        _query.Page = new AlertEventPage
        {
            Items = new[]
            {
                NewSummary(eventId: 11, severity: AlertSeverity.Warning, lifecycle: AlertLifecycleStatus.Acknowledged)
            }
        };
        _lifecycle.Lifecycle = new AlertEventLifecycle
        {
            EventId = 11,
            Status = AlertLifecycleStatus.Acknowledged,
            AcknowledgedAt = DateTimeOffset.UtcNow,
            AcknowledgedBy = "operator",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var response = await _client.PostAsJsonAsync("/api/v1/admin/observability/alerts/11/acknowledge",
            new { note = "watching" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _audit.Recorded.Should().ContainSingle();
        _audit.Recorded[0].Action.Should().Be("alert.acknowledge");
        _audit.Recorded[0].ResourceType.Should().Be("alert_event");
        _audit.Recorded[0].ResourceId.Should().Be("11");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    public async Task SuppressAlert_RequiresFutureSuppressUntil()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/observability/alerts/4/suppress",
            new { suppressUntil = DateTimeOffset.UtcNow.AddHours(-1) });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _audit.Recorded.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/resolve")]
    public async Task ResolveAlert_ReturnsNotFound_WhenLifecycleMissing()
    {
        // Lifecycle store returns null — event does not exist.
        _lifecycle.Lifecycle = null;

        var response = await _client.PostAsJsonAsync("/api/v1/admin/observability/alerts/999/resolve",
            new { note = (string?)null });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _audit.Recorded.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/alerts")]
    public async Task ListAlerts_RejectsNumericSeverityFilter()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/alerts?severity=99");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/alerts/{eventId}")]
    public async Task GetSingleAlert_Returns200_WhenFound()
    {
        _query.Page = new AlertEventPage
        {
            Items = new[] { NewSummary(eventId: 13, severity: AlertSeverity.Warning, lifecycle: AlertLifecycleStatus.Open) }
        };

        var response = await _client.GetAsync("/api/v1/admin/observability/alerts/13");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static AlertEventSummary NewSummary(long eventId, AlertSeverity severity, AlertLifecycleStatus lifecycle)
        => new()
        {
            EventId = eventId,
            RuleId = 1,
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 1,
            TriggerType = AlertTriggerType.Enter,
            Severity = severity,
            OccurredAt = DateTimeOffset.UtcNow,
            IncidentStatus = AlertIncidentStatus.Started,
            IncidentDurationMs = 0,
            LifecycleStatus = lifecycle
        };

    private sealed class StubAlertEventQuery : IAlertEventQuery
    {
        public AlertEventPage Page { get; set; } = new() { Items = Array.Empty<AlertEventSummary>() };

        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(Page);

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertEventSummary?>(Page.Items.FirstOrDefault(item => item.EventId == eventId));
    }

    private sealed class StubAlertLifecycleStore : IAlertLifecycleStore
    {
        public AlertEventLifecycle? Lifecycle { get; set; }

        public Task<AlertEventLifecycle?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => Task.FromResult(Lifecycle);

        public Task<AlertEventLifecycle?> AcknowledgeAsync(long eventId, string actor, string? note,
            DateTimeOffset acknowledgedAt, CancellationToken cancellationToken = default)
            => Task.FromResult(Lifecycle);

        public Task<AlertEventLifecycle?> SuppressAsync(long eventId, string actor, DateTimeOffset suppressUntil,
            string? note, DateTimeOffset suppressedAt, CancellationToken cancellationToken = default)
            => Task.FromResult(Lifecycle);

        public Task<AlertEventLifecycle?> ResolveAsync(long eventId, string actor, string? note,
            DateTimeOffset resolvedAt, CancellationToken cancellationToken = default)
            => Task.FromResult(Lifecycle);
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

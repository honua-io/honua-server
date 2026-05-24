// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// HTTP integration tests for the Console Operate unified event timeline,
/// log buffer, and audit log endpoints (#1168).
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class ObservabilityEventEndpointsTests : IAsyncLifetime
{
    private readonly StubAuditReader _auditReader = new();
    private readonly StubOperateFeed _feed = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public ObservabilityEventEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IDatabaseMigrationRunner>();
                services.AddSingleton<IDatabaseMigrationRunner>(new NoopMigrationRunner());

                services.RemoveAll<IAuditLogReader>();
                services.RemoveAll<IOperateEventFeed>();
                services.AddSingleton<IAuditLogReader>(_auditReader);
                services.AddSingleton<IOperateEventFeed>(_feed);
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/events")]
    public async Task ListEvents_NormalizesItemsAndPropagatesPartialResult()
    {
        _feed.Page = new OperateEventPage
        {
            Items = new[]
            {
                new OperateEvent
                {
                    EventId = "audit:1",
                    Kind = OperateEventKind.Audit,
                    Severity = OperateEventSeverity.Notice,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Title = "alert.acknowledge",
                    Actor = "alice",
                    CorrelationId = "corr-1",
                    ResourceRef = "alert_event/42"
                }
            },
            PartialResult = true,
            SourceErrors = new Dictionary<OperateEventKind, string>
            {
                [OperateEventKind.Job] = "job source unavailable"
            }
        };

        var response = await _client.GetAsync("/api/v1/admin/observability/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("kind").GetString().Should().Be("audit");
        items[0].GetProperty("severity").GetString().Should().Be("notice");
        items[0].GetProperty("resourceRef").GetString().Should().Be("alert_event/42");
        doc.RootElement.GetProperty("partialResult").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("sourceErrors").GetProperty("job").GetString().Should().Be("job source unavailable");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/audit")]
    public async Task ListAudit_ReturnsItemsAndPaginates()
    {
        _auditReader.Page = new AuditEventPage
        {
            Items = new[]
            {
                new AuditEventRecord
                {
                    AuditId = 1,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction,
                    Actor = "bob",
                    ActorType = AuditActorType.UserId,
                    ResourceType = "alert_event",
                    ResourceId = "42",
                    Action = "alert.resolve",
                    Outcome = AuditOutcome.Success,
                    CorrelationId = "corr-2"
                }
            }
        };

        var response = await _client.GetAsync("/api/v1/admin/observability/audit?pageSize=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("action").GetString().Should().Be("alert.resolve");
        items[0].GetProperty("actor").GetString().Should().Be("bob");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/observability/logs")]
    public async Task ListLogs_ReturnsInstanceMetadataAndItems()
    {
        var response = await _client.GetAsync("/api/v1/admin/observability/logs");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("instanceId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("capacity").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    private sealed class StubAuditReader : IAuditLogReader
    {
        public AuditEventPage Page { get; set; } = new() { Items = Array.Empty<AuditEventRecord>() };

        public Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(Page);
    }

    private sealed class StubOperateFeed : IOperateEventFeed
    {
        public OperateEventPage Page { get; set; } = new() { Items = Array.Empty<OperateEvent>() };

        public Task<OperateEventPage> ListAsync(OperateEventFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(Page);
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

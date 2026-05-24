// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Server.Features.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Observability;

/// <summary>
/// Unit tests for <see cref="LocalOperateEventFeed"/> covering fan-out, sort,
/// partial-failure handling, and post-merge filters (#1168).
/// </summary>
public sealed class LocalOperateEventFeedTests
{
    [UnitTest]
    public async Task ListAsync_MergesAndSortsAcrossSources()
    {
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                new AlertEventSummary
                {
                    EventId = 10,
                    RuleId = 1,
                    ServiceId = "svc",
                    LayerId = 1,
                    ObjectId = 1,
                    TriggerType = AlertTriggerType.Enter,
                    Severity = AlertSeverity.Warning,
                    OccurredAt = new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero),
                    IncidentStatus = AlertIncidentStatus.Started,
                    IncidentDurationMs = 0,
                    LifecycleStatus = AlertLifecycleStatus.Open
                }
            }
        };

        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1,
                    Timestamp = new DateTimeOffset(2026, 5, 20, 10, 5, 0, TimeSpan.Zero),
                    EventType = AuditEventType.AdminAction,
                    Actor = "alice",
                    ActorType = AuditActorType.UserId,
                    ResourceType = "alert_event",
                    ResourceId = "10",
                    Action = "alert.acknowledge",
                    Outcome = AuditOutcome.Success,
                    CorrelationId = "corr-1"
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery, auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { PageSize = 10 });

        page.Items.Should().HaveCount(2);
        page.Items[0].Kind.Should().Be(OperateEventKind.Audit);
        page.Items[1].Kind.Should().Be(OperateEventKind.Alert);
        page.PartialResult.Should().BeFalse();
    }

    [UnitTest]
    public async Task ListAsync_RespectsKindFilter()
    {
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                new AlertEventSummary
                {
                    EventId = 1, RuleId = 1, ServiceId = "svc", LayerId = 1, ObjectId = 1,
                    TriggerType = AlertTriggerType.Enter, Severity = AlertSeverity.Info,
                    OccurredAt = DateTimeOffset.UtcNow,
                    IncidentStatus = AlertIncidentStatus.Started,
                    IncidentDurationMs = 0, LifecycleStatus = AlertLifecycleStatus.Open
                }
            }
        };

        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 5, Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.Authentication, Actor = "bob",
                    ActorType = AuditActorType.UserId, ResourceType = "session",
                    Action = "auth.success", Outcome = AuditOutcome.Success,
                    CorrelationId = "corr-2"
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery, auditReader);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Audit]
        });

        page.Items.Should().HaveCount(1);
        page.Items[0].Kind.Should().Be(OperateEventKind.Audit);
    }

    [UnitTest]
    public async Task ListAsync_ReturnsPartialResult_WhenOneSourceThrows()
    {
        var failingAlerts = new ThrowingAlertQuery();
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1, Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "alert_event",
                    Action = "alert.resolve", Outcome = AuditOutcome.Success, CorrelationId = "corr-1"
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, failingAlerts, auditReader);
        var page = await feed.ListAsync(new OperateEventFilter());

        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().NotBeNull();
        page.SourceErrors!.Should().ContainKey(OperateEventKind.Alert);
        page.Items.Should().HaveCount(1);
        page.Items[0].Kind.Should().Be(OperateEventKind.Audit);
    }

    [UnitTest]
    public async Task ListAsync_AppliesMinimumSeverityFilter()
    {
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                NewSummary(1, AlertSeverity.Info, DateTimeOffset.UtcNow.AddMinutes(-2)),
                NewSummary(2, AlertSeverity.Critical, DateTimeOffset.UtcNow.AddMinutes(-1))
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery);
        var page = await feed.ListAsync(new OperateEventFilter { MinimumSeverity = OperateEventSeverity.Warning });

        page.Items.Should().HaveCount(1);
        page.Items[0].Severity.Should().Be(OperateEventSeverity.Critical);
    }

    [UnitTest]
    public async Task ListAsync_FiltersByCorrelationIdAcrossSources()
    {
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1, Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "alert_event",
                    Action = "alert.acknowledge", Outcome = AuditOutcome.Success,
                    CorrelationId = "trace-1"
                },
                new AuditEventRecord
                {
                    AuditId = 2, Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "alert_event",
                    Action = "alert.resolve", Outcome = AuditOutcome.Success,
                    CorrelationId = "trace-2"
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, auditReader: auditReader);
        var page = await feed.ListAsync(new OperateEventFilter { CorrelationId = "trace-1" });

        page.Items.Should().HaveCount(1);
        page.Items[0].EventId.Should().Be("audit:1");
    }

    private static AlertEventSummary NewSummary(long id, AlertSeverity severity, DateTimeOffset occurredAt)
        => new()
        {
            EventId = id,
            RuleId = 1,
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 1,
            TriggerType = AlertTriggerType.Enter,
            Severity = severity,
            OccurredAt = occurredAt,
            IncidentStatus = AlertIncidentStatus.Started,
            IncidentDurationMs = 0,
            LifecycleStatus = AlertLifecycleStatus.Open
        };

    private sealed class FakeAlertQuery : IAlertEventQuery
    {
        public List<AlertEventSummary> Items { get; } = new();

        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new AlertEventPage { Items = Items, NextCursor = null });

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertEventSummary?>(Items.FirstOrDefault(item => item.EventId == eventId));
    }

    private sealed class ThrowingAlertQuery : IAlertEventQuery
    {
        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
            => Task.FromException<AlertEventPage>(new InvalidOperationException("alert store offline"));

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("alert store offline");
    }

    private sealed class FakeAuditReader : IAuditLogReader
    {
        public List<AuditEventRecord> Items { get; } = new();

        public Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
            => Task.FromResult(new AuditEventPage { Items = Items, NextCursor = null });
    }
}

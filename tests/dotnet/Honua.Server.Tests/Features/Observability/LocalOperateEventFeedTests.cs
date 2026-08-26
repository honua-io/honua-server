// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.ControlPlane;
using Honua.Infrastructure.Monitoring;
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
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeFalse();
    }

    [UnitTest]
    public async Task ListAsync_SingleSourceExceedsPageSize_ReportsHasMore()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                NewSummary(1, AlertSeverity.Warning, now),
                NewSummary(2, AlertSeverity.Warning, now.AddMinutes(-1))
            }
        };
        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Alert],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("alert:1");
        page.HasMore.Should().BeTrue();
        page.Truncated.Should().BeTrue();
    }

    [UnitTest]
    public async Task ListAsync_MultipleSourcesExceedMergedPageSize_ReportsHasMore()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items = { NewSummary(1, AlertSeverity.Warning, now.AddMinutes(-1)) }
        };
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1,
                    Timestamp = now,
                    EventType = AuditEventType.AdminAction,
                    Actor = "alice",
                    ActorType = AuditActorType.UserId,
                    ResourceType = "service",
                    ResourceId = "svc",
                    Action = "service.update",
                    Outcome = AuditOutcome.Success,
                    CorrelationId = "corr-1"
                }
            }
        };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            alertQuery,
            auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { PageSize = 1 });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("audit:1");
        page.HasMore.Should().BeTrue();
        page.Truncated.Should().BeTrue();
    }

    [UnitTest]
    public async Task ListAsync_AlertCursorCycle_MarksSourcePartialAndTruncated()
    {
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            new CyclingAlertQuery());

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Alert],
            PageSize = 10
        });

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Alert);
    }

    [UnitTest]
    public async Task ListAsync_AlertLaterPageFailure_PreservesEarlierRowsAndMarksPartial()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            MaxPageSize = 1,
            ThrowOnListCall = 2,
            Items =
            {
                NewSummary(1, AlertSeverity.Warning, now),
                NewSummary(2, AlertSeverity.Warning, now.AddMinutes(-1))
            }
        };
        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Alert],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("alert:1");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Alert);
    }

    [UnitTest]
    public async Task ListAsync_AuditLaterPageFailure_PreservesEarlierRowsAndMarksPartial()
    {
        var now = DateTimeOffset.UtcNow;
        var auditReader = new FakeAuditReader
        {
            MaxPageSize = 1,
            ThrowOnListCall = 2,
            Items =
            {
                NewAudit(1, now),
                NewAudit(2, now.AddMinutes(-1))
            }
        };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            auditReader: auditReader);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Audit],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("audit:1");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Audit);
    }

    [UnitTest]
    public async Task ListAsync_EmptySuccessfulSource_RemainsInQueriedSources()
    {
        var alertQuery = new FakeAlertQuery
        {
            Items = { NewSummary(10, AlertSeverity.Warning, DateTimeOffset.UtcNow) }
        };
        var auditReader = new FakeAuditReader();
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            alertQuery,
            auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { PageSize = 10 });

        page.Items.Should().ContainSingle().Which.Kind.Should().Be(OperateEventKind.Alert);
        page.QueriedSources.Should().BeEquivalentTo(
            [OperateEventKind.Alert, OperateEventKind.Audit],
            "a successful empty component still contributes to composite coverage");
        page.SourceErrors.Should().BeNull();
    }

    [UnitTest]
    public async Task ListAsync_AuditDetails_ProjectsOnlyBoundedCausalMetadata()
    {
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 7,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction,
                    Actor = "ops-findings",
                    ActorType = AuditActorType.System,
                    ResourceType = "operation_autonomy",
                    ResourceId = "finding-7",
                    Action = "operation.auto_verified",
                    Outcome = AuditOutcome.Success,
                    CorrelationId = "operation-7",
                    Details = """
                        {
                          "findingId":"finding-7",
                          "rule":"alert-dispatch-backlog",
                          "kind":"AdminConfigChange",
                          "actionDiscriminator":"alerts.redrive_dead_letters",
                          "mode":"AutoApply",
                          "status":"Converged",
                          "operationId":"operation-7",
                          "killSwitchEnabled":false,
                          "evidenceRefs":[
                            "metric:honua.alert.dispatch.dead_letters",
                            "audit:2",
                            "audit:3",
                            "audit:4",
                            "audit:5",
                            "audit:6",
                            "audit:7",
                            "secret=do-not-project"
                          ],
                          "executionPayload":{"action":"hidden"},
                          "password":"do-not-project",
                          "sql":"select secret from credentials",
                          "stackTrace":"provider internals",
                          "message":"raw provider error"
                        }
                        """,
                },
            },
        };

        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            auditReader: auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { Kinds = [OperateEventKind.Audit] });

        var detailsJson = page.Items.Should().ContainSingle().Subject.DetailsJson;
        detailsJson.Should().NotBeNullOrWhiteSpace();
        detailsJson!.Length.Should().BeLessThanOrEqualTo(2048);
        using var details = JsonDocument.Parse(detailsJson);
        var root = details.RootElement;
        root.GetProperty("findingId").GetString().Should().Be("finding-7");
        root.GetProperty("rule").GetString().Should().Be("alert-dispatch-backlog");
        root.GetProperty("actionDiscriminator").GetString().Should().Be("alerts.redrive_dead_letters");
        root.GetProperty("status").GetString().Should().Be("Converged");
        root.GetProperty("evidenceRefs").EnumerateArray().Select(item => item.GetString())
            .Should().Equal(
                "metric:honua.alert.dispatch.dead_letters",
                "audit:2",
                "audit:3",
                "audit:4",
                "audit:5",
                "audit:6");
        root.TryGetProperty("executionPayload", out _).Should().BeFalse();
        root.TryGetProperty("password", out _).Should().BeFalse();
        root.TryGetProperty("sql", out _).Should().BeFalse();
        root.TryGetProperty("stackTrace", out _).Should().BeFalse();
        root.TryGetProperty("message", out _).Should().BeFalse();
    }

    [UnitTest]
    public async Task ListAsync_AutonomyAuditDetails_ProjectsBoundedSanitizedMessageAndDerivedState()
    {
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 9,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction,
                    Actor = "ops-findings",
                    ActorType = AuditActorType.System,
                    ResourceType = "operation_autonomy",
                    ResourceId = "finding-9",
                    Action = "operation.auto_verified",
                    Outcome = AuditOutcome.Success,
                    CorrelationId = "operation-9",
                    Details = """
                        {"rule":"alert-dispatch-backlog","findingId":"finding-9","message":"backlog converged across two observations"}
                        """,
                },
            },
        };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            auditReader: auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { Kinds = [OperateEventKind.Audit] });

        using var details = JsonDocument.Parse(page.Items.Should().ContainSingle().Subject.DetailsJson!);
        details.RootElement.GetProperty("mode").GetString().Should().Be("AutoApply");
        details.RootElement.GetProperty("status").GetString().Should().Be("Converged");
        details.RootElement.GetProperty("message").GetString().Should().Be("backlog converged across two observations");
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    public async Task ListAsync_AuditDetails_MalformedOrNonObject_IsNotProjected(string details)
    {
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 8,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction,
                    Actor = "ops-findings",
                    ActorType = AuditActorType.System,
                    ResourceType = "operation_autonomy",
                    ResourceId = "finding-8",
                    Action = "operation.auto_failed",
                    Outcome = AuditOutcome.Failure,
                    CorrelationId = "operation-8",
                    Details = details,
                },
            },
        };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            auditReader: auditReader);

        var page = await feed.ListAsync(new OperateEventFilter { Kinds = [OperateEventKind.Audit] });

        page.Items.Should().ContainSingle().Which.DetailsJson.Should().BeNull();
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
    public async Task ListAsync_AlertSource_AppliesSeverityBeforeSourcePagination()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                NewSummary(1, AlertSeverity.Info, now),
                NewSummary(2, AlertSeverity.Critical, now.AddMinutes(-1))
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            MinimumSeverity = OperateEventSeverity.Warning,
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].EventId.Should().Be("alert:2");
        alertQuery.SeenFilters.Should().Contain(filter =>
            filter.Severities != null &&
            filter.Severities.Contains(AlertSeverity.Warning) &&
            filter.Severities.Contains(AlertSeverity.Critical));
    }

    [UnitTest]
    public async Task ListAsync_AlertSource_ResourceRefFetchesDirectEvent()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items =
            {
                NewSummary(1, AlertSeverity.Critical, now),
                NewSummary(2, AlertSeverity.Warning, now.AddMinutes(-1))
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            ResourceRef = "alert/2",
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].EventId.Should().Be("alert:2");
        alertQuery.ListCalls.Should().Be(0);
        alertQuery.GetCalls.Should().Be(1);
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

    [UnitTest]
    public async Task ListAsync_AuditSource_ProjectsStructuredDetailsJson()
    {
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 9,
                    Timestamp = DateTimeOffset.UtcNow,
                    EventType = AuditEventType.AdminAction,
                    Actor = "database-migration-runner",
                    ActorType = AuditActorType.System,
                    ResourceType = "database_migration",
                    ResourceId = "schema-migration-test",
                    Action = "migration.backup_hook",
                    Outcome = AuditOutcome.Failure,
                    CorrelationId = "schema-migration-test",
                    Details = """
                        {"outcome":"failed","durationMilliseconds":42,"stderr":"pg_dump: permission denied"}
                        """
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, auditReader: auditReader);
        var page = await feed.ListAsync(new OperateEventFilter { ResourceRef = "database_migration/schema-migration-test" });

        var item = page.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be(OperateEventKind.Audit);
        item.Title.Should().Be("migration.backup_hook");
        item.Severity.Should().Be(OperateEventSeverity.Error);
        item.DetailsJson.Should().NotBeNull();
        item.DetailsJson!.Should().Contain("\"durationMilliseconds\":42");
        item.DetailsJson.Should().Contain("pg_dump: permission denied");
    }

    [UnitTest]
    public async Task ListAsync_AuditSource_AppliesResourceRefBeforeSourcePagination()
    {
        var now = DateTimeOffset.UtcNow;
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1, Timestamp = now,
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "job",
                    ResourceId = "new", Action = "job.retry",
                    Outcome = AuditOutcome.Success, CorrelationId = "corr-1"
                },
                new AuditEventRecord
                {
                    AuditId = 2, Timestamp = now.AddMinutes(-1),
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "alert_event",
                    ResourceId = "42", Action = "alert.resolve",
                    Outcome = AuditOutcome.Success, CorrelationId = "corr-2"
                }
            }
        };

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, auditReader: auditReader);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            ResourceRef = "alert_event/42",
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].EventId.Should().Be("audit:2");
        auditReader.SeenFilters.Should().Contain(filter =>
            filter.ResourceType == "alert_event" && filter.ResourceId == "42");
    }

    [UnitTest]
    public async Task ListAsync_AuditSource_TypeOnlyResourceRef_MatchesEveryResourceIdOfType()
    {
        var now = DateTimeOffset.UtcNow;
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 20, Timestamp = now,
                    EventType = AuditEventType.AdminAction, Actor = "ops-findings",
                    ActorType = AuditActorType.System, ResourceType = "operation_autonomy",
                    ResourceId = "finding-a", Action = "operation.auto_applied",
                    Outcome = AuditOutcome.Success, CorrelationId = "corr-a",
                },
                new AuditEventRecord
                {
                    AuditId = 21, Timestamp = now.AddMinutes(-1),
                    EventType = AuditEventType.AdminAction, Actor = "ops-findings",
                    ActorType = AuditActorType.System, ResourceType = "operation_autonomy",
                    ResourceId = "finding-b", Action = "operation.auto_failed",
                    Outcome = AuditOutcome.Failure, CorrelationId = "corr-b",
                },
                new AuditEventRecord
                {
                    AuditId = 22, Timestamp = now.AddMinutes(-2),
                    EventType = AuditEventType.ConfigChange, Actor = "human-admin",
                    ActorType = AuditActorType.UserId, ResourceType = "ops_autonomy_policy",
                    ResourceId = "alert-dispatch-backlog", Action = "ops_autonomy.policy.update",
                    Outcome = AuditOutcome.Success, CorrelationId = "corr-policy",
                },
            },
        };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            auditReader: auditReader);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            ResourceRef = "operation_autonomy",
            Kinds = [OperateEventKind.Audit],
            PageSize = 10,
        });

        page.Items.Select(item => item.ResourceRef).Should().Equal(
            "operation_autonomy/finding-a",
            "operation_autonomy/finding-b");
        auditReader.SeenFilters.Should().ContainSingle(filter =>
            filter.ResourceType == "operation_autonomy" && filter.ResourceId == null);
    }

    [UnitTest]
    public async Task ListAsync_ServiceFilter_DropsUnscopedSources()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items = { NewSummary(1, AlertSeverity.Warning, now) }
        };
        var auditReader = new FakeAuditReader
        {
            Items =
            {
                new AuditEventRecord
                {
                    AuditId = 1, Timestamp = now.AddMinutes(1),
                    EventType = AuditEventType.AdminAction, Actor = "alice",
                    ActorType = AuditActorType.UserId, ResourceType = "alert_event",
                    ResourceId = "1", Action = "alert.acknowledge",
                    Outcome = AuditOutcome.Success, CorrelationId = "corr-1"
                }
            }
        };
        var store = new FakeProgressStore();
        store.Add(NewJob("job-1", now.AddMinutes(2)));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, alertQuery, auditReader, store);
        var page = await feed.ListAsync(new OperateEventFilter { ServiceId = "svc", PageSize = 10 });

        page.Items.Should().ContainSingle();
        page.Items[0].Kind.Should().Be(OperateEventKind.Alert);
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeFalse("the service filter excludes progress-derived jobs");
        page.PartialResult.Should().BeFalse();
        page.SourceErrors.Should().BeNull();
    }

    [UnitTest]
    public async Task ListAsync_JobSource_KeepsNewestWhenIdsExceedPageSize()
    {
        // Unordered active-ids: oldest first, newest last. The bug was truncating
        // before sorting, which silently dropped the newest job.
        var store = new FakeProgressStore();
        store.Add(NewJob("job-old", DateTimeOffset.UtcNow.AddMinutes(-30)));
        store.Add(NewJob("job-mid", DateTimeOffset.UtcNow.AddMinutes(-15)));
        store.Add(NewJob("job-new", DateTimeOffset.UtcNow.AddMinutes(-1)));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, progressStore: store);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 1
        });

        page.Items.Should().HaveCount(1);
        page.Items[0].EventId.Should().Be("job:job-new");
        page.HasMore.Should().BeTrue();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_EmptyLocalProgressSnapshotIsPartialAndTruncated()
    {
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: new FakeProgressStore());

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job)
            .WhoseValue.Should().Be("job source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_JobSource_LocalEnumerationCanOmitReachableRecordWithoutClaimingHasMore()
    {
        var store = new FakeProgressStore();
        store.AddOutOfBand("peer-or-evicted", NewJob("peer-or-evicted", DateTimeOffset.UtcNow));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: store);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().BeEmpty("the process-local enumeration omitted the reachable record");
        page.HasMore.Should().BeFalse("unknown coverage does not prove another matching row");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_ClusterWideProgressEnumerationCanBeComplete()
    {
        var store = new FakeProgressStore
        {
            ProvidesClusterWideActiveOperationEnumeration = true
        };
        store.Add(NewJob("cluster-wide", DateTimeOffset.UtcNow));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: store);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("cluster-wide");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeFalse();
        page.PartialResult.Should().BeFalse();
        page.SourceErrors.Should().BeNull();
    }

    [UnitTest]
    public async Task ListAsync_JobSource_ClusterWideEnumerationNullRaceIsPartialAndTruncated()
    {
        var store = new FakeProgressStore
        {
            ProvidesClusterWideActiveOperationEnumeration = true
        };
        store.AddMissingActiveId("expired-after-enumeration");
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: store);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_OperationIdFiltersDirectly()
    {
        // The requested job is NOT in the active-ids list (operation finished
        // and was evicted), but is reachable via direct GetProgressAsync.
        var store = new FakeProgressStore();
        store.Add(NewJob("active-1", DateTimeOffset.UtcNow.AddMinutes(-2)));
        store.AddOutOfBand("specific", NewJob("specific", DateTimeOffset.UtcNow.AddMinutes(-5)));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, progressStore: store);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            OperationId = "specific",
            PageSize = 10
        });

        page.Items.Should().HaveCount(1);
        page.Items[0].EventId.Should().Be("job:specific");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
        store.ActiveIdsCalls.Should().Be(0, "direct lookup must skip the unordered active-ids list");
    }

    [UnitTest]
    public async Task ListAsync_JobSource_AppliesSeverityBeforeTrimming()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new FakeProgressStore();
        store.Add(NewJob("job-processing", now));
        store.Add(NewJob("job-failed", now.AddMinutes(-1), OperationStatus.Failed));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, progressStore: store);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            MinimumSeverity = OperateEventSeverity.Error,
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].EventId.Should().Be("job:job-failed");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
    }

    [UnitTest]
    public async Task ListAsync_JobSource_MergesDurableAndProgressSources()
    {
        var now = DateTimeOffset.UtcNow;
        var progressStore = new FakeProgressStore();
        progressStore.Add(NewJob("progress-only", now.AddMinutes(-1)));
        progressStore.Add(NewJob("durable-job", now.AddMinutes(-2)));

        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(NewExecutionJob("durable-job", now, ExecutionJobStatus.Running));

        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: jobStore);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        var operationIds = page.Items.Select(item => item.OperationId).ToArray();
        operationIds.Should().HaveCount(2);
        operationIds.Should().Contain("durable-job");
        operationIds.Should().Contain("progress-only");
        page.Items.Should().ContainSingle(item => item.OperationId == "durable-job");
        page.Items.Single(item => item.OperationId == "durable-job").Title.Should().Be("Geoprocessing Running");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue("the process-local progress source may omit other jobs");
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_OperationIdFallsBackToProgressWhenDurableMissing()
    {
        var progressStore = new FakeProgressStore();
        progressStore.AddOutOfBand("specific", NewJob("specific", DateTimeOffset.UtcNow.AddMinutes(-5)));

        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: new FakeExecutionJobStore());
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            OperationId = "specific",
            PageSize = 10
        });

        page.Items.Should().ContainSingle();
        page.Items[0].EventId.Should().Be("job:specific");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        progressStore.ActiveIdsCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DirectDurableHitSkipsIncompleteProgressAndRemainsComplete()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(NewExecutionJob("durable-hit", now, ExecutionJobStatus.Running));
        var progressStore = new FakeProgressStore();
        progressStore.AddOutOfBand("durable-hit", NewJob("durable-hit", now.AddMinutes(-1)));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            OperationId = "durable-hit",
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("durable-hit");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeFalse();
        page.PartialResult.Should().BeFalse();
        page.SourceErrors.Should().BeNull();
        progressStore.ProgressGetCalls.Should().Be(0);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableResourceRefMatchesJobMetadata()
    {
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(NewExecutionJob(
            "durable-resource",
            DateTimeOffset.UtcNow,
            ExecutionJobStatus.Running,
            new Dictionary<string, string>
            {
                [ExecutionJobParameterKeys.ResourceRefs] = "service/parcels|layer/parcels"
            }));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            ResourceRef = "service/parcels",
            PageSize = 10
        });

        page.Items.Should().ContainSingle();
        page.Items[0].OperationId.Should().Be("durable-resource");
        page.Items[0].ResourceRef.Should().Be("job/durable-resource");
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableFromFilterUsesEventTimestamp()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(
            NewExecutionJob(
                "created-newer-not-updated",
                now.AddMinutes(-20),
                ExecutionJobStatus.Running,
                createdAt: now.AddMinutes(-21)),
            NewExecutionJob(
                "created-older-updated-recently",
                now,
                ExecutionJobStatus.Running,
                createdAt: now.AddHours(-2)));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            From = now.AddMinutes(-1),
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].OperationId.Should().Be("created-older-updated-recently");
        jobStore.SeenQueries.Should().OnlyContain(query => query.CreatedFrom == null);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableScanBudgetExhaustionMarksPartialWithoutClaimingMoreMatches()
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = new List<ExecutionJobRecord>(201)
        {
            NewExecutionJob(
                "matching-head",
                now,
                ExecutionJobStatus.Running,
                createdAt: now.AddMinutes(-2))
        };
        for (var index = 0; index < 199; index++)
        {
            jobs.Add(NewExecutionJob(
                $"filtered-{index:D3}",
                now.AddHours(-2),
                ExecutionJobStatus.Running,
                createdAt: now.AddHours(-3).AddSeconds(-index)));
        }

        jobs.Add(NewExecutionJob(
            "matching-beyond-budget",
            now.AddSeconds(-30),
            ExecutionJobStatus.Running,
            createdAt: now.AddHours(-4)));

        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(jobs.ToArray());
        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            From = now.AddMinutes(-1),
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("matching-head");
        page.HasMore.Should().BeFalse("the unscanned cursor does not prove another matching row");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
        jobStore.SeenQueries.Should().HaveCount(4);
        jobStore.SeenQueries[^1].Cursor.Should().Be("150");
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableSortsFullBoundedScanByEventTimeBeforeTrimming()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(
            NewExecutionJob(
                "created-newest-updated-old",
                now.AddHours(-1),
                ExecutionJobStatus.Running,
                createdAt: now.AddHours(-2)),
            NewExecutionJob(
                "created-middle-updated-old",
                now.AddMinutes(-50),
                ExecutionJobStatus.Running,
                createdAt: now.AddHours(-3)),
            NewExecutionJob(
                "created-oldest-updated-newest",
                now,
                ExecutionJobStatus.Running,
                createdAt: now.AddHours(-4)));
        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("created-oldest-updated-newest");
        page.HasMore.Should().BeTrue();
    }

    [UnitTest]
    public async Task ListAsync_ReleaseSource_SortsWholeSnapshotBeforeTrimming()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new ReleaseTimelineBuffer();
        releases.Append(NewRelease("newest", now));
        releases.Append(NewRelease("oldest", now.AddHours(-2)));
        releases.Append(NewRelease("middle", now.AddHours(-1)));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            releaseTimeline: releases);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Release],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("release:newest");
        page.HasMore.Should().BeTrue();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue("the release timeline is per-instance");
        page.SourceErrors.Should().ContainKey(OperateEventKind.Release)
            .WhoseValue.Should().Be("release source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_ReleaseSource_PerInstanceSnapshotIsPartialAndTruncated()
    {
        var releases = new ReleaseTimelineBuffer();
        releases.Append(NewRelease("local-only", DateTimeOffset.UtcNow));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            releaseTimeline: releases);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Release],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("release:local-only");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Release)
            .WhoseValue.Should().Be("release source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_ReleaseSource_EmptyFreshBufferIsPartialAndTruncated()
    {
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            releaseTimeline: new ReleaseTimelineBuffer());

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Release],
            PageSize = 10
        });

        page.Items.Should().BeEmpty();
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Release)
            .WhoseValue.Should().Be("release source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_ReleaseSource_EvictedFilteredHistoryIsPartialAndTruncated()
    {
        var now = DateTimeOffset.UtcNow;
        var releases = new ReleaseTimelineBuffer(capacity: 2);
        releases.Append(NewRelease("evicted-match", now.AddHours(-3)));
        releases.Append(NewRelease("retained-match", now.AddHours(-2)));
        releases.Append(NewRelease("filtered-newer", now));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            releaseTimeline: releases);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Release],
            To = now.AddHours(-1),
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.EventId.Should().Be("release:retained-match");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Release)
            .WhoseValue.Should().Be("release source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_MixedReleaseAndDurableSource_RemainsPartialAndTruncated()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items = { NewSummary(1, AlertSeverity.Warning, now) }
        };
        var releases = new ReleaseTimelineBuffer();
        releases.Append(NewRelease("local", now.AddMinutes(-1)));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            alertQuery: alertQuery,
            releaseTimeline: releases);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Alert, OperateEventKind.Release],
            PageSize = 10
        });

        page.Items.Should().HaveCount(2);
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Release)
            .WhoseValue.Should().Be("release source incomplete");
    }

    [UnitTest]
    public async Task ListAsync_ExcludedReleaseSource_DoesNotDegradeCompleteSource()
    {
        var now = DateTimeOffset.UtcNow;
        var alertQuery = new FakeAlertQuery
        {
            Items = { NewSummary(1, AlertSeverity.Warning, now) }
        };
        var releases = new ReleaseTimelineBuffer();
        releases.Append(NewRelease("excluded", now.AddMinutes(-1)));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            alertQuery: alertQuery,
            releaseTimeline: releases);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Alert],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.Kind.Should().Be(OperateEventKind.Alert);
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeFalse();
        page.PartialResult.Should().BeFalse();
        page.SourceErrors.Should().BeNull();
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableFailurePreservesProgressFallback()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore { ThrowOnQueryCall = 1 };
        var progressStore = new FakeProgressStore();
        progressStore.Add(NewJob("progress-fallback", now));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("progress-fallback");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableLaterPageFailurePreservesEarlierRows()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore
        {
            MaxPageSize = 1,
            ThrowOnQueryCall = 2
        };
        jobStore.Add(
            NewExecutionJob("preserved", now, ExecutionJobStatus.Running),
            NewExecutionJob("unavailable", now.AddMinutes(-1), ExecutionJobStatus.Running));
        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 1
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("preserved");
        page.HasMore.Should().BeFalse();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_PerIdProgressFailurePreservesSiblingAndMarksPartial()
    {
        var now = DateTimeOffset.UtcNow;
        var progressStore = new FakeProgressStore();
        progressStore.Add(NewJob("unavailable", now));
        progressStore.Add(NewJob("available", now.AddMinutes(-1)));
        progressStore.ThrowOnGetIds.Add("unavailable");
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("available");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_ProgressEnumerationFailurePreservesDurableRows()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(NewExecutionJob("durable-preserved", now, ExecutionJobStatus.Running));
        var progressStore = new FakeProgressStore { ThrowOnActiveIds = true };
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("durable-preserved");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DirectProgressFailureMarksPartial()
    {
        var progressStore = new FakeProgressStore();
        progressStore.ThrowOnGetIds.Add("direct-unavailable");
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            OperationId = "direct-unavailable",
            PageSize = 10
        });

        page.Items.Should().BeEmpty();
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DirectDurableFailureFallsBackToProgressAndMarksPartial()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore { ThrowOnGet = true };
        var progressStore = new FakeProgressStore();
        progressStore.AddOutOfBand("direct-fallback", NewJob("direct-fallback", now));
        var feed = new LocalOperateEventFeed(
            NullLogger<LocalOperateEventFeed>.Instance,
            progressStore: progressStore,
            jobStore: jobStore);

        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            OperationId = "direct-fallback",
            PageSize = 10
        });

        page.Items.Should().ContainSingle().Which.OperationId.Should().Be("direct-fallback");
        page.Truncated.Should().BeTrue();
        page.PartialResult.Should().BeTrue();
        page.SourceErrors.Should().ContainKey(OperateEventKind.Job);
    }

    [UnitTest]
    public async Task ListAsync_JobSource_DurableSeverityFilterAppliesBeforeTrimming()
    {
        var now = DateTimeOffset.UtcNow;
        var jobStore = new FakeExecutionJobStore();
        jobStore.Add(
            NewExecutionJob("created-newer-running", now, ExecutionJobStatus.Running),
            NewExecutionJob("created-older-failed", now.AddMinutes(-1), ExecutionJobStatus.Failed));

        var feed = new LocalOperateEventFeed(NullLogger<LocalOperateEventFeed>.Instance, jobStore: jobStore);
        var page = await feed.ListAsync(new OperateEventFilter
        {
            Kinds = [OperateEventKind.Job],
            MinimumSeverity = OperateEventSeverity.Error,
            PageSize = 1
        });

        page.Items.Should().ContainSingle();
        page.Items[0].OperationId.Should().Be("created-older-failed");
        jobStore.SeenQueries.Should().ContainSingle();
        jobStore.SeenQueries[0].Statuses.Should().Equal(ExecutionJobStatus.Failed);
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

    private static AuditEventRecord NewAudit(long id, DateTimeOffset occurredAt)
        => new()
        {
            AuditId = id,
            Timestamp = occurredAt,
            EventType = AuditEventType.AdminAction,
            Actor = "alice",
            ActorType = AuditActorType.UserId,
            ResourceType = "service",
            ResourceId = "svc",
            Action = "service.update",
            Outcome = AuditOutcome.Success,
            CorrelationId = "corr-" + id.ToString(CultureInfo.InvariantCulture)
        };

    private static FakeProgress NewJob(
        string operationId,
        DateTimeOffset startedAt,
        OperationStatus status = OperationStatus.Processing)
        => new()
        {
            OperationId = operationId,
            Type = OperationType.Import,
            Status = status,
            StartedAt = startedAt
        };

    private static ExecutionJobRecord NewExecutionJob(
        string operationId,
        DateTimeOffset updatedAt,
        ExecutionJobStatus status,
        IReadOnlyDictionary<string, string>? parameters = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? completedAt = null)
        => new()
        {
            OperationId = operationId,
            Status = status,
            CreatedAt = createdAt ?? updatedAt.AddMinutes(-1),
            UpdatedAt = updatedAt,
            CompletedAt = completedAt,
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                CorrelationId = "corr-" + operationId
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "workload-" + operationId,
                Parameters = parameters ?? new Dictionary<string, string>()
            }
        };

    private static OperateEvent NewRelease(string id, DateTimeOffset occurredAt)
        => new()
        {
            EventId = "release:" + id,
            Kind = OperateEventKind.Release,
            Severity = OperateEventSeverity.Notice,
            OccurredAt = occurredAt,
            Title = "Release " + id,
            ReleaseId = id,
            ResourceRef = "release/" + id
        };

    private sealed class FakeAlertQuery : IAlertEventQuery
    {
        public List<AlertEventSummary> Items { get; } = new();
        public List<AlertEventFilter> SeenFilters { get; } = new();
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int? MaxPageSize { get; init; }
        public int? ThrowOnListCall { get; init; }

        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            SeenFilters.Add(filter);
            if (ListCalls == ThrowOnListCall)
            {
                return Task.FromException<AlertEventPage>(new InvalidOperationException("alert page unavailable"));
            }

            var filtered = Items.AsEnumerable();
            if (filter.From is { } from)
            {
                filtered = filtered.Where(item => item.OccurredAt >= from);
            }

            if (filter.To is { } to)
            {
                filtered = filtered.Where(item => item.OccurredAt < to);
            }

            if (!string.IsNullOrWhiteSpace(filter.ServiceId))
            {
                filtered = filtered.Where(item => item.ServiceId == filter.ServiceId);
            }

            if (filter.LayerId is { } layerId)
            {
                filtered = filtered.Where(item => item.LayerId == layerId);
            }

            if (filter.ObjectId is { } objectId)
            {
                filtered = filtered.Where(item => item.ObjectId == objectId);
            }

            if (filter.RuleId is { } ruleId)
            {
                filtered = filtered.Where(item => item.RuleId == ruleId);
            }

            if (filter.Severities is { Count: > 0 } severities)
            {
                filtered = filtered.Where(item => severities.Contains(item.Severity));
            }

            var ordered = filtered
                .OrderByDescending(item => item.OccurredAt)
                .ThenByDescending(item => item.EventId)
                .ToArray();
            var offset = ParseCursor(filter.Cursor);
            var pageSize = Math.Max(1, Math.Min(filter.PageSize, MaxPageSize ?? int.MaxValue));
            var page = ordered.Skip(offset).Take(pageSize).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < ordered.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;

            return Task.FromResult(new AlertEventPage { Items = page, NextCursor = nextCursor });
        }

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult<AlertEventSummary?>(Items.FirstOrDefault(item => item.EventId == eventId));
        }
    }

    private sealed class CyclingAlertQuery : IAlertEventQuery
    {
        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
        {
            var nextCursor = filter.Cursor switch
            {
                null => "cursor-a",
                "cursor-a" => "cursor-b",
                _ => "cursor-a"
            };
            return Task.FromResult(new AlertEventPage
            {
                Items = Array.Empty<AlertEventSummary>(),
                NextCursor = nextCursor
            });
        }

        public Task<AlertEventSummary?> GetAsync(long eventId, CancellationToken cancellationToken = default)
            => Task.FromResult<AlertEventSummary?>(null);
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
        public List<AuditLogFilter> SeenFilters { get; } = new();
        public int? MaxPageSize { get; init; }
        public int? ThrowOnListCall { get; init; }

        public Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
        {
            SeenFilters.Add(filter);
            if (SeenFilters.Count == ThrowOnListCall)
            {
                return Task.FromException<AuditEventPage>(new InvalidOperationException("audit page unavailable"));
            }

            var filtered = Items.AsEnumerable();
            if (filter.From is { } from)
            {
                filtered = filtered.Where(item => item.Timestamp >= from);
            }

            if (filter.To is { } to)
            {
                filtered = filtered.Where(item => item.Timestamp < to);
            }

            if (!string.IsNullOrWhiteSpace(filter.Actor))
            {
                filtered = filtered.Where(item => item.Actor == filter.Actor);
            }

            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                filtered = filtered.Where(item => item.ResourceType == filter.ResourceType);
            }

            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                filtered = filtered.Where(item => item.ResourceId == filter.ResourceId);
            }

            if (!string.IsNullOrWhiteSpace(filter.Action))
            {
                filtered = filtered.Where(item => item.Action == filter.Action);
            }

            if (!string.IsNullOrWhiteSpace(filter.CorrelationId))
            {
                filtered = filtered.Where(item => item.CorrelationId == filter.CorrelationId);
            }

            if (filter.Outcomes is { Count: > 0 } outcomes)
            {
                filtered = filtered.Where(item => outcomes.Contains(item.Outcome));
            }

            var ordered = filtered
                .OrderByDescending(item => item.Timestamp)
                .ThenByDescending(item => item.AuditId)
                .ToArray();
            var offset = ParseCursor(filter.Cursor);
            var pageSize = Math.Max(1, Math.Min(filter.PageSize, MaxPageSize ?? int.MaxValue));
            var page = ordered.Skip(offset).Take(pageSize).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < ordered.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;

            return Task.FromResult(new AuditEventPage { Items = page, NextCursor = nextCursor });
        }
    }

    private static int ParseCursor(string? cursor)
        => int.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) ? offset : 0;

    private sealed class FakeProgressStore : IUniversalProgressStore
    {
        private readonly List<string> _activeIds = new();
        private readonly Dictionary<string, IOperationProgress> _records = new();
        public int ActiveIdsCalls { get; private set; }
        public int ProgressGetCalls { get; private set; }
        public HashSet<string> ThrowOnGetIds { get; } = new(StringComparer.Ordinal);
        public bool ThrowOnActiveIds { get; init; }
        public bool ProvidesClusterWideActiveOperationEnumeration { get; init; }

        public void Add(IOperationProgress progress)
        {
            _activeIds.Add(progress.OperationId);
            _records[progress.OperationId] = progress;
        }

        public void AddOutOfBand(string operationId, IOperationProgress progress)
        {
            // Reachable via direct fetch but NOT in the active-ids enumeration.
            _records[operationId] = progress;
        }

        public void AddMissingActiveId(string operationId)
            => _activeIds.Add(operationId);

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            ActiveIdsCalls++;
            if (ThrowOnActiveIds)
            {
                return Task.FromException<IReadOnlyList<string>>(new InvalidOperationException("progress enumeration unavailable"));
            }

            return Task.FromResult<IReadOnlyList<string>>(_activeIds.ToList());
        }

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            ProgressGetCalls++;
            return ThrowOnGetIds.Contains(operationId)
                ? Task.FromException<IOperationProgress?>(new InvalidOperationException("progress record unavailable"))
                : Task.FromResult(_records.GetValueOrDefault(operationId));
        }

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_records.GetValueOrDefault(operationId) as TProgress);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId,
            IOperationProgress progress,
            OperationStatus expectedStatus,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => throw new NotSupportedException();
    }

    private sealed class FakeExecutionJobStore : IExecutionJobStore
    {
        private readonly Dictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);
        public List<ExecutionJobQuery> SeenQueries { get; } = new();
        public int? ThrowOnQueryCall { get; init; }
        public int? MaxPageSize { get; init; }
        public bool ThrowOnGet { get; init; }

        public void Add(params ExecutionJobRecord[] records)
        {
            foreach (var record in records)
            {
                _records[record.OperationId] = record;
            }
        }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            if (_records.ContainsKey(job.OperationId))
            {
                return Task.FromResult(false);
            }

            _records[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => ThrowOnGet
                ? Task.FromException<ExecutionJobRecord?>(new InvalidOperationException("durable job unavailable"))
                : Task.FromResult(_records.GetValueOrDefault(operationId));

        public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _records[job.OperationId] = job;
            return Task.FromResult(true);
        }

        public Task<ExecutionJobPage> QueryAsync(ExecutionJobQuery query, CancellationToken cancellationToken = default)
        {
            SeenQueries.Add(query);
            if (SeenQueries.Count == ThrowOnQueryCall)
            {
                return Task.FromException<ExecutionJobPage>(new InvalidOperationException("durable job page unavailable"));
            }

            var items = _records.Values
                .Where(job => query.Statuses.Count == 0 || query.Statuses.Contains(job.Status))
                .Where(job => !query.Kind.HasValue || job.Spec.Kind == query.Kind.Value)
                .Where(job => string.IsNullOrWhiteSpace(query.Backend) || string.Equals(query.Backend, job.Spec.Backend, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.Queue) || string.Equals(query.Queue, ExecutionJobMetadata.ResolveQueue(job), StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.RequestedBy) || string.Equals(query.RequestedBy, job.Audit.RequestedBy, StringComparison.OrdinalIgnoreCase))
                .Where(job => string.IsNullOrWhiteSpace(query.CorrelationId) || string.Equals(query.CorrelationId, job.Audit.CorrelationId, StringComparison.Ordinal))
                .Where(job => string.IsNullOrWhiteSpace(query.TraceId) || MatchesParameter(job, ExecutionJobParameterKeys.TraceId, query.TraceId))
                .Where(job => string.IsNullOrWhiteSpace(query.ResourceRef) || MatchesResourceRef(job, query.ResourceRef))
                .Where(job => string.IsNullOrWhiteSpace(query.ReleaseId) || MatchesParameter(job, ExecutionJobParameterKeys.ReleaseId, query.ReleaseId))
                .Where(job => string.IsNullOrWhiteSpace(query.ChangeSetId) || MatchesParameter(job, ExecutionJobParameterKeys.ChangeSetId, query.ChangeSetId))
                .Where(job => !query.CreatedFrom.HasValue || job.CreatedAt >= query.CreatedFrom.Value)
                .Where(job => !query.CreatedTo.HasValue || job.CreatedAt < query.CreatedTo.Value)
                .OrderByDescending(job => job.CreatedAt)
                .ThenByDescending(job => job.OperationId, StringComparer.Ordinal)
                .ToArray();
            var offset = ParseCursor(query.Cursor);
            var limit = Math.Max(1, Math.Min(query.Limit, MaxPageSize ?? int.MaxValue));
            var page = items.Skip(offset).Take(limit).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < items.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;

            return Task.FromResult(new ExecutionJobPage { Items = page, NextCursor = nextCursor });
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, int? limit = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExecutionJobRecord>>(_records.Values.ToArray());

        private static bool MatchesParameter(ExecutionJobRecord job, string key, string? expected)
            => job.Spec.Parameters.TryGetValue(key, out var actual) &&
               string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);

        private static bool MatchesResourceRef(ExecutionJobRecord job, string? expected)
            => job.Spec.Parameters.TryGetValue(ExecutionJobParameterKeys.ResourceRefs, out var raw) &&
               raw.Split(
                    ExecutionJobParameterKeys.MetadataListSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(expected, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FakeProgress : IOperationProgress
    {
        public required string OperationId { get; init; }
        public required OperationType Type { get; init; }
        public required OperationStatus Status { get; init; }
        public double? PercentComplete { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;
        public string? ErrorMessage { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
        public string? CurrentPhase { get; init; }
    }
}

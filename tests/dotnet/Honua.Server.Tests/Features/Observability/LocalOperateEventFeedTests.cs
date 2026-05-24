// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Observability.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
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
        progressStore.ActiveIdsCalls.Should().Be(0);
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

    private sealed class FakeAlertQuery : IAlertEventQuery
    {
        public List<AlertEventSummary> Items { get; } = new();
        public List<AlertEventFilter> SeenFilters { get; } = new();
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }

        public Task<AlertEventPage> ListAsync(AlertEventFilter filter, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            SeenFilters.Add(filter);

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
            var pageSize = Math.Max(1, filter.PageSize);
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

        public Task<AuditEventPage> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
        {
            SeenFilters.Add(filter);

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
            var pageSize = Math.Max(1, filter.PageSize);
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

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
        {
            ActiveIdsCalls++;
            return Task.FromResult<IReadOnlyList<string>>(_activeIds.ToList());
        }

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_records.GetValueOrDefault(operationId));

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_records.GetValueOrDefault(operationId) as TProgress);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
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
            => Task.FromResult(_records.GetValueOrDefault(operationId));

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
            var limit = Math.Max(1, query.Limit);
            var page = items.Skip(offset).Take(limit).ToArray();
            var nextOffset = offset + page.Length;
            var nextCursor = nextOffset < items.Length
                ? nextOffset.ToString(CultureInfo.InvariantCulture)
                : null;

            return Task.FromResult(new ExecutionJobPage { Items = page, NextCursor = nextCursor });
        }

        public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
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

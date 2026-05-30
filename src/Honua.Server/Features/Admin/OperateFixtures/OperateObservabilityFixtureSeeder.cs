// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.ControlPlane;
using Honua.Infrastructure.Monitoring;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Admin.OperateFixtures;

internal interface IOperateObservabilityFixtureSeeder
{
    Task<OperateObservabilityFixtureSeedResponse> SeedAsync(
        string profile,
        CancellationToken cancellationToken = default);
}

internal sealed partial class OperateObservabilityFixtureSeeder(
    IServiceProvider services,
    TimeProvider timeProvider,
    ILogger<OperateObservabilityFixtureSeeder> logger) : IOperateObservabilityFixtureSeeder
{
    private const string HarborWkt =
        "POLYGON((-157.8720 21.2980,-157.8580 21.2980,-157.8580 21.3090,-157.8720 21.3090,-157.8720 21.2980))";
    private const string FixtureBackend = "fixture-postgres";
    private const string FixtureQueue = "operate-observability";
    private const string FixtureEnvironment = "console-testcontainers";
    private const string FixtureServer = "honua-server-fixture-1";
    private const string FixtureReleaseId = "release-operate-fixture-2026.05";
    private const string FixtureChangeSetId = "changeset-operate-fixture-001";

    public async Task<OperateObservabilityFixtureSeedResponse> SeedAsync(
        string profile,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(profile, OperateObservabilityFixtureConstants.Profile, StringComparison.Ordinal))
        {
            throw new ArgumentException("Unsupported Operate observability fixture profile.", nameof(profile));
        }

        var seededAt = timeProvider.GetUtcNow();
        var alertAdminStore = services.GetRequiredService<IAlertAdminStore>();
        var geometryService = services.GetRequiredService<IGeometryService>();
        var alertEventStore = services.GetRequiredService<IAlertEventStore>();
        var lifecycleStore = services.GetRequiredService<IAlertLifecycleStore>();
        var connectionProvider = services.GetRequiredService<IDatabaseConnectionProvider>();
        var jobStore = services.GetRequiredService<IExecutionJobStore>();
        var logStore = services.GetRequiredService<IExecutionLogStore>();
        var recentErrors = services.GetRequiredService<RecentErrorBuffer>();

        var zone = await EnsureZoneAsync(alertAdminStore, geometryService, profile, cancellationToken)
            .ConfigureAwait(false);
        var rule = await EnsureRuleAsync(alertAdminStore, zone, profile, cancellationToken).ConfigureAwait(false);
        var alertSeed = await EnsureAlertEventsAsync(
                alertEventStore,
                lifecycleStore,
                connectionProvider,
                rule,
                zone,
                profile,
                seededAt,
                cancellationToken)
            .ConfigureAwait(false);

        await EnsureAuditEventsAsync(connectionProvider, zone, rule, alertSeed, profile, seededAt, cancellationToken)
            .ConfigureAwait(false);
        SeedRecentLogs(recentErrors, seededAt);

        var jobs = await EnsureJobsAsync(jobStore, logStore, alertSeed.OpenAlertEventId, profile, seededAt, cancellationToken)
            .ConfigureAwait(false);
        var investigation = await EnsureInvestigationAsync(
                connectionProvider,
                alertSeed.OpenAlertEventId,
                seededAt,
                cancellationToken)
            .ConfigureAwait(false);

        FixtureSeeded(
            logger,
            profile,
            zone.ZoneId,
            rule.RuleId,
            jobs.Length,
            investigation.InvestigationId);

        return new OperateObservabilityFixtureSeedResponse
        {
            Profile = profile,
            SourceHydrated = true,
            SeededAt = seededAt,
            ServiceId = OperateObservabilityFixtureConstants.ServiceId,
            LayerId = OperateObservabilityFixtureConstants.LayerId,
            CorrelationId = OperateObservabilityFixtureConstants.CorrelationId,
            TraceId = OperateObservabilityFixtureConstants.TraceId,
            Actor = OperateObservabilityFixtureConstants.Actor,
            Alerts = alertSeed,
            Jobs = jobs,
            Investigation = investigation,
            Links = BuildLinks()
        };
    }

    private static async Task<AlertZoneDefinition> EnsureZoneAsync(
        IAlertAdminStore store,
        IGeometryService geometryService,
        string profile,
        CancellationToken cancellationToken)
    {
        if (!AlertZoneGeometryParser.TryParseWkt(HarborWkt, 4326, geometryService, out var geometry, out var error))
        {
            throw new InvalidOperationException("Operate observability fixture zone geometry is invalid: " + error);
        }

        var existing = (await store.ListZonesAsync(OperateObservabilityFixtureConstants.ServiceId, cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(zone => string.Equals(
                zone.ZoneName,
                OperateObservabilityFixtureConstants.ZoneName,
                StringComparison.Ordinal));

        var zoneDefinition = new AlertZoneDefinition
        {
            ZoneId = existing?.ZoneId ?? 0,
            ServiceId = OperateObservabilityFixtureConstants.ServiceId,
            ZoneName = OperateObservabilityFixtureConstants.ZoneName,
            Geometry = geometry,
            GeometrySrid = 4326,
            IsActive = true,
            Metadata = ImmutableDictionary.CreateRange<string, string?>([
                KeyValuePair.Create<string, string?>("fixtureProfile", profile),
                KeyValuePair.Create<string, string?>("sourceHydrated", "true"),
                KeyValuePair.Create<string, string?>("purpose", "console-testcontainers-operate")
            ])
        };

        if (existing is null)
        {
            return await store.CreateZoneAsync(zoneDefinition, cancellationToken).ConfigureAwait(false);
        }

        return await store.UpdateZoneAsync(zoneDefinition, cancellationToken).ConfigureAwait(false)
               ?? zoneDefinition;
    }

    private static async Task<AlertRuleDefinition> EnsureRuleAsync(
        IAlertAdminStore store,
        AlertZoneDefinition zone,
        string profile,
        CancellationToken cancellationToken)
    {
        var existing = (await store.ListRulesAsync(
                    OperateObservabilityFixtureConstants.ServiceId,
                    OperateObservabilityFixtureConstants.LayerId,
                    cancellationToken)
                .ConfigureAwait(false))
            .FirstOrDefault(rule => string.Equals(
                rule.RuleName,
                OperateObservabilityFixtureConstants.RuleName,
                StringComparison.Ordinal));

        var ruleDefinition = new AlertRuleDefinition
        {
            RuleId = existing?.RuleId ?? 0,
            ServiceId = OperateObservabilityFixtureConstants.ServiceId,
            LayerId = OperateObservabilityFixtureConstants.LayerId,
            ZoneId = zone.ZoneId,
            RuleName = OperateObservabilityFixtureConstants.RuleName,
            TriggerType = AlertTriggerType.Enter,
            ConditionsJson = $$"""{"fixtureProfile":"{{profile}}","sourceHydrated":true,"zoneName":"{{OperateObservabilityFixtureConstants.ZoneName}}","predicate":"feature_enters_harbor"}""",
            CooldownSeconds = 60,
            Severity = AlertSeverity.Warning,
            EditionRequired = AlertEdition.Pro,
            Channels = ImmutableArray.Create(AlertChannelType.WebSocket),
            IsActive = true
        };

        if (existing is null)
        {
            return await store.CreateRuleAsync(ruleDefinition, cancellationToken).ConfigureAwait(false);
        }

        return await store.UpdateRuleAsync(ruleDefinition, cancellationToken).ConfigureAwait(false)
               ?? ruleDefinition;
    }

    private static async Task<OperateObservabilityAlertFixtureSeed> EnsureAlertEventsAsync(
        IAlertEventStore store,
        IAlertLifecycleStore lifecycleStore,
        IDatabaseConnectionProvider connectionProvider,
        AlertRuleDefinition rule,
        AlertZoneDefinition zone,
        string profile,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        var openDedupeKey = profile + ":alert-open";
        var resolvedDedupeKey = profile + ":alert-resolved";
        var openId = await EnsureAlertEventAsync(
                store,
                connectionProvider,
                openDedupeKey,
                rule,
                zone,
                objectId: 4201,
                generation: 1209001,
                severity: AlertSeverity.Warning,
                incidentStatus: AlertIncidentStatus.Started,
                occurredAt: seededAt.AddMinutes(-9),
                payloadJson: BuildAlertPayload(profile, "open", null),
                cancellationToken)
            .ConfigureAwait(false);
        var resolvedId = await EnsureAlertEventAsync(
                store,
                connectionProvider,
                resolvedDedupeKey,
                rule,
                zone,
                objectId: 4202,
                generation: 1209002,
                severity: AlertSeverity.Critical,
                incidentStatus: AlertIncidentStatus.Ended,
                occurredAt: seededAt.AddMinutes(-18),
                payloadJson: BuildAlertPayload(profile, "resolved", "00:07:00"),
                cancellationToken)
            .ConfigureAwait(false);

        await ClearAlertLifecycleAsync(connectionProvider, openId, cancellationToken).ConfigureAwait(false);
        _ = await lifecycleStore.ResolveAsync(
                resolvedId,
                OperateObservabilityFixtureConstants.Actor,
                "Resolved by Operate observability fixture seed.",
                seededAt.AddMinutes(-7),
                cancellationToken)
            .ConfigureAwait(false);

        return new OperateObservabilityAlertFixtureSeed
        {
            ZoneId = zone.ZoneId,
            ZoneName = zone.ZoneName,
            RuleId = rule.RuleId,
            RuleName = rule.RuleName,
            OpenAlertEventId = openId,
            ResolvedAlertEventId = resolvedId,
            DedupeKeys = [openDedupeKey, resolvedDedupeKey]
        };
    }

    private static async Task<long> EnsureAlertEventAsync(
        IAlertEventStore store,
        IDatabaseConnectionProvider connectionProvider,
        string dedupeKey,
        AlertRuleDefinition rule,
        AlertZoneDefinition zone,
        long objectId,
        long generation,
        AlertSeverity severity,
        AlertIncidentStatus incidentStatus,
        DateTimeOffset occurredAt,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var created = await store.TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = dedupeKey,
            RuleId = rule.RuleId,
            ZoneId = zone.ZoneId,
            ServiceId = OperateObservabilityFixtureConstants.ServiceId,
            LayerId = OperateObservabilityFixtureConstants.LayerId,
            ObjectId = objectId,
            TriggerType = AlertTriggerType.Enter,
            Generation = generation,
            Severity = severity,
            OccurredAt = occurredAt,
            PayloadJson = payloadJson,
            IncidentStatus = incidentStatus,
            IncidentDurationMs = incidentStatus == AlertIncidentStatus.Ended
                    ? (long)TimeSpan.FromMinutes(7).TotalMilliseconds
                    : 0
        },
            cancellationToken).ConfigureAwait(false);
        if (created.HasValue)
        {
            return created.Value;
        }

        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id FROM honua.alert_events WHERE dedupe_key = @dedupe_key";
        command.Parameters.Add(Parameter("dedupe_key", NpgsqlDbType.Text, dedupeKey));
        var existing = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return existing is long eventId
            ? eventId
            : throw new InvalidOperationException("Alert fixture event deduplicated but no existing row was found.");
    }

    private static async Task ClearAlertLifecycleAsync(
        IDatabaseConnectionProvider connectionProvider,
        long eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM honua.alert_event_lifecycle WHERE event_id = @event_id";
        command.Parameters.Add(Parameter("event_id", NpgsqlDbType.Bigint, eventId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAuditEventsAsync(
        IDatabaseConnectionProvider connectionProvider,
        AlertZoneDefinition zone,
        AlertRuleDefinition rule,
        OperateObservabilityAlertFixtureSeed alertSeed,
        string profile,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        await InsertAuditIfMissingAsync(
                connectionProvider,
                seededAt.AddMinutes(-25),
                "ConfigChange",
                "alert_zone",
                zone.ZoneId.ToString(CultureInfo.InvariantCulture),
                "alert_zone.create",
                BuildAuditDetails(profile, "alert_zone", zone.ZoneId.ToString(CultureInfo.InvariantCulture)),
                cancellationToken)
            .ConfigureAwait(false);
        await InsertAuditIfMissingAsync(
                connectionProvider,
                seededAt.AddMinutes(-24),
                "ConfigChange",
                "alert_rule",
                rule.RuleId.ToString(CultureInfo.InvariantCulture),
                "alert_rule.create",
                BuildAuditDetails(profile, "alert_rule", rule.RuleId.ToString(CultureInfo.InvariantCulture)),
                cancellationToken)
            .ConfigureAwait(false);
        await InsertAuditIfMissingAsync(
                connectionProvider,
                seededAt.AddMinutes(-6),
                "AdminAction",
                "investigation",
                OperateObservabilityFixtureConstants.InvestigationId,
                "investigation.create",
                BuildAuditDetails(profile, "investigation", OperateObservabilityFixtureConstants.InvestigationId),
                cancellationToken)
            .ConfigureAwait(false);
        await InsertAuditIfMissingAsync(
                connectionProvider,
                seededAt.AddMinutes(-5),
                "AdminAction",
                "alert_event",
                alertSeed.OpenAlertEventId.ToString(CultureInfo.InvariantCulture),
                "alert.triage",
                BuildAuditDetails(profile, "alert_event", alertSeed.OpenAlertEventId.ToString(CultureInfo.InvariantCulture)),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task InsertAuditIfMissingAsync(
        IDatabaseConnectionProvider connectionProvider,
        DateTimeOffset timestamp,
        string eventType,
        string resourceType,
        string resourceId,
        string action,
        string details,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO honua.audit_log (
                timestamp, event_type, actor, actor_type, resource_type, resource_id,
                action, outcome, correlation_id, remote_ip, user_agent, details)
            SELECT
                @timestamp, @event_type, @actor, @actor_type, @resource_type, @resource_id,
                @action, @outcome, @correlation_id, @remote_ip, @user_agent, @details
            WHERE NOT EXISTS (
                SELECT 1
                FROM honua.audit_log
                WHERE action = @action
                  AND resource_type = @resource_type
                  AND resource_id = @resource_id
                  AND correlation_id = @correlation_id)
            """;
        command.Parameters.Add(Parameter("timestamp", NpgsqlDbType.TimestampTz, timestamp));
        command.Parameters.Add(Parameter("event_type", NpgsqlDbType.Text, eventType));
        command.Parameters.Add(Parameter("actor", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.Actor));
        command.Parameters.Add(Parameter("actor_type", NpgsqlDbType.Text, "UserId"));
        command.Parameters.Add(Parameter("resource_type", NpgsqlDbType.Text, resourceType));
        command.Parameters.Add(Parameter("resource_id", NpgsqlDbType.Text, resourceId));
        command.Parameters.Add(Parameter("action", NpgsqlDbType.Text, action));
        command.Parameters.Add(Parameter("outcome", NpgsqlDbType.Text, "Success"));
        command.Parameters.Add(Parameter("correlation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.CorrelationId));
        command.Parameters.Add(Parameter("remote_ip", NpgsqlDbType.Text, "127.0.0.1"));
        command.Parameters.Add(Parameter("user_agent", NpgsqlDbType.Text, "honua-console-testcontainers"));
        command.Parameters.Add(Parameter("details", NpgsqlDbType.Text, details));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void SeedRecentLogs(RecentErrorBuffer buffer, DateTimeOffset seededAt)
    {
        buffer.RecordSeed(new RecentErrorEntry
        {
            Timestamp = seededAt.AddMinutes(-4),
            CorrelationId = OperateObservabilityFixtureConstants.CorrelationId,
            Path = "/api/v1/admin/observability/alerts",
            StatusCode = StatusCodes.Status503ServiceUnavailable,
            Message = "Fixture alert notification delivery retried after transient backend outage."
        });
        buffer.RecordSeed(new RecentErrorEntry
        {
            Timestamp = seededAt.AddMinutes(-3),
            CorrelationId = OperateObservabilityFixtureConstants.CorrelationId,
            Path = "/api/v1/admin/jobs/" + OperateObservabilityFixtureConstants.FailedJobId,
            StatusCode = StatusCodes.Status500InternalServerError,
            Message = "Fixture durable job failed while publishing a synthetic artifact."
        });
    }

    private async Task<OperateObservabilityJobFixtureSeed[]> EnsureJobsAsync(
        IExecutionJobStore jobStore,
        IExecutionLogStore logStore,
        long alertEventId,
        string profile,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        var alertRef = "alert/" + alertEventId.ToString(CultureInfo.InvariantCulture);
        var succeeded = BuildJob(
            OperateObservabilityFixtureConstants.SucceededJobId,
            ExecutionJobStatus.Succeeded,
            "Fixture export completed",
            null,
            percentComplete: 100,
            createdAt: seededAt.AddMinutes(-28),
            updatedAt: seededAt.AddMinutes(-16),
            alertRef,
            profile);
        var failed = BuildJob(
            OperateObservabilityFixtureConstants.FailedJobId,
            ExecutionJobStatus.Failed,
            "Artifact publishing",
            "Synthetic artifact publication failed after retry budget was exhausted.",
            percentComplete: 63,
            createdAt: seededAt.AddMinutes(-22),
            updatedAt: seededAt.AddMinutes(-8),
            alertRef,
            profile);

        await UpsertJobAsync(jobStore, succeeded, cancellationToken).ConfigureAwait(false);
        await UpsertJobAsync(jobStore, failed, cancellationToken).ConfigureAwait(false);
        await SeedJobLogsAsync(
            logStore,
            succeeded.OperationId,
            profile,
            [
                BuildLog(seededAt.AddMinutes(-27), ExecutionLogLevel.Info, "Fixture job accepted by durable queue.", "Queued", "succeeded-queued"),
                BuildLog(seededAt.AddMinutes(-20), ExecutionLogLevel.Info, "Fixture export wrote two synthetic features.", "Running", "succeeded-running"),
                BuildLog(seededAt.AddMinutes(-16), ExecutionLogLevel.Info, "Fixture job completed successfully.", "Completed", "succeeded-completed")
            ],
            cancellationToken).ConfigureAwait(false);
        await SeedJobLogsAsync(
            logStore,
            failed.OperationId,
            profile,
            [
                BuildLog(seededAt.AddMinutes(-21), ExecutionLogLevel.Info, "Fixture job started artifact publishing.", "Running", "failed-started"),
                BuildLog(seededAt.AddMinutes(-12), ExecutionLogLevel.Warning, "Fixture artifact publish retry budget is nearly exhausted.", "Artifact publishing", "failed-warning"),
                BuildLog(seededAt.AddMinutes(-8), ExecutionLogLevel.Error, "Fixture artifact publication failed after retries.", "Failed", "failed-error")
            ],
            cancellationToken).ConfigureAwait(false);

        return
        [
            MapJobSeed(succeeded),
            MapJobSeed(failed)
        ];
    }

    private static ExecutionJobRecord BuildJob(
        string jobId,
        ExecutionJobStatus status,
        string phase,
        string? errorMessage,
        double percentComplete,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string alertRef,
        string profile)
    {
        var resourceRefs = string.Join(
            ExecutionJobParameterKeys.MetadataListSeparator,
            OperateObservabilityFixtureConstants.ServiceId,
            "layer/" + OperateObservabilityFixtureConstants.LayerId.ToString(CultureInfo.InvariantCulture),
            alertRef);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.DefinitionId] = "operate-observability-fixture",
            [ExecutionJobParameterKeys.GeoprocessingPlanId] = "operate-observability-fixture-plan",
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = "fixture.operate-observability",
            [ExecutionJobParameterKeys.GeoprocessingOutputArtifactKinds] = "FeatureLayer",
            [ExecutionJobParameterKeys.Queue] = FixtureQueue,
            [ExecutionJobParameterKeys.ResourceRefs] = resourceRefs,
            [ExecutionJobParameterKeys.Environment] = FixtureEnvironment,
            [ExecutionJobParameterKeys.Server] = FixtureServer,
            [ExecutionJobParameterKeys.ReleaseId] = FixtureReleaseId,
            [ExecutionJobParameterKeys.ChangeSetId] = FixtureChangeSetId,
            [ExecutionJobParameterKeys.AlertId] = alertRef,
            [ExecutionJobParameterKeys.TraceId] = OperateObservabilityFixtureConstants.TraceId,
            [OperateObservabilityFixtureConstants.FixtureProfileParameter] = profile,
            [OperateObservabilityFixtureConstants.FixtureSourceHydratedParameter] = "true"
        };

        return new ExecutionJobRecord
        {
            OperationId = jobId,
            Status = status,
            Priority = OperationPriority.High,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            CompletedAt = updatedAt,
            ProviderOperationId = "provider-" + jobId,
            PercentComplete = percentComplete,
            CurrentPhase = phase,
            ErrorMessage = errorMessage,
            Warnings = status == ExecutionJobStatus.Failed
                ? ["Artifact publication retry budget exhausted."]
                : [],
            Audit = new OperationAuditInfo
            {
                RequestedBy = OperateObservabilityFixtureConstants.Actor,
                Reason = "Seed Console Operate observability Testcontainers fixture.",
                IdempotencyKey = profile + ":" + jobId,
                CorrelationId = OperateObservabilityFixtureConstants.CorrelationId
            },
            Spec = new ExecutionJobSpec
            {
                WorkloadId = "operate-observability-fixture",
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = FixtureBackend,
                Kind = ExecutionJobKind.Geoprocessing,
                WorkloadName = jobId == OperateObservabilityFixtureConstants.SucceededJobId
                    ? "Operate fixture export"
                    : "Operate fixture artifact publish",
                Artifact = "ghcr.io/honua/fixture/operate-observability:console-testcontainers-v1",
                RuntimeProfile = "console-testcontainers",
                Parameters = parameters
            },
            ArtifactReferences = jobId == OperateObservabilityFixtureConstants.SucceededJobId
                ? ["https://example.test/honua/fixtures/operate/succeeded-output.geojson"]
                : ["https://example.test/honua/fixtures/operate/failed-diagnostics.json"]
        };
    }

    private static async Task UpsertJobAsync(
        IExecutionJobStore jobStore,
        ExecutionJobRecord job,
        CancellationToken cancellationToken)
    {
        var existing = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            var created = await jobStore.TryCreateAsync(job, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (created)
            {
                return;
            }

            existing = await jobStore.GetAsync(job.OperationId, cancellationToken).ConfigureAwait(false);
        }

        await jobStore.SetAsync(job with { Version = existing?.Version ?? job.Version }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SeedJobLogsAsync(
        IExecutionLogStore logStore,
        string jobId,
        string profile,
        IReadOnlyList<ExecutionLogEntry> entries,
        CancellationToken cancellationToken)
    {
        if (services.GetService<PostgresOperateFixtureExecutionStore>() is { } fixtureStore)
        {
            await fixtureStore.ReplaceLogsAsync(jobId, profile, entries, cancellationToken).ConfigureAwait(false);
            return;
        }

        var existing = await logStore.GetLogsAsync(jobId, cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var fixtureLogId = entry.Metadata?["fixtureLogId"];
            if (!string.IsNullOrWhiteSpace(fixtureLogId) &&
                existing.Any(candidate =>
                    candidate.Metadata is not null &&
                    candidate.Metadata.TryGetValue("fixtureLogId", out var candidateLogId) &&
                    string.Equals(candidateLogId, fixtureLogId, StringComparison.Ordinal)))
            {
                continue;
            }

            await logStore.AppendAsync(jobId, entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ExecutionLogEntry BuildLog(
        DateTimeOffset timestamp,
        ExecutionLogLevel level,
        string message,
        string phase,
        string fixtureLogId)
        => new()
        {
            Timestamp = timestamp,
            Level = level,
            Message = message,
            Phase = phase,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fixtureLogId"] = fixtureLogId,
                [OperateObservabilityFixtureConstants.FixtureProfileParameter] = OperateObservabilityFixtureConstants.Profile,
                [OperateObservabilityFixtureConstants.FixtureSourceHydratedParameter] = "true",
                ["correlationId"] = OperateObservabilityFixtureConstants.CorrelationId,
                ["traceId"] = OperateObservabilityFixtureConstants.TraceId
            }
        };

    private static OperateObservabilityJobFixtureSeed MapJobSeed(ExecutionJobRecord job)
    {
        var detail = "/api/v1/admin/jobs/" + job.OperationId;
        return new OperateObservabilityJobFixtureSeed
        {
            JobId = job.OperationId,
            Status = job.Status.ToString(),
            DetailLink = detail,
            LogsLink = detail + "/logs",
            ArtifactsLink = detail + "/artifacts"
        };
    }

    private static async Task<OperateObservabilityInvestigationFixtureSeed> EnsureInvestigationAsync(
        IDatabaseConnectionProvider connectionProvider,
        long openAlertEventId,
        DateTimeOffset seededAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, transaction, """
            INSERT INTO honua.investigations (
                investigation_id, title, status, created_at, created_by, updated_at, summary)
            VALUES (
                @investigation_id, @title, @status, @created_at, @created_by, @updated_at, @summary)
            ON CONFLICT (investigation_id) DO UPDATE SET
                title = EXCLUDED.title,
                status = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at,
                summary = EXCLUDED.summary
            """,
            command =>
            {
                command.Parameters.Add(Parameter("investigation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.InvestigationId));
                command.Parameters.Add(Parameter("title", NpgsqlDbType.Text, "Operate fixture harbor alert triage"));
                command.Parameters.Add(Parameter("status", NpgsqlDbType.Smallint, (short)InvestigationStatus.Open));
                command.Parameters.Add(Parameter("created_at", NpgsqlDbType.TimestampTz, seededAt.AddMinutes(-6)));
                command.Parameters.Add(Parameter("created_by", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.Actor));
                command.Parameters.Add(Parameter("updated_at", NpgsqlDbType.TimestampTz, seededAt.AddMinutes(-2)));
                command.Parameters.Add(Parameter(
                    "summary",
                    NpgsqlDbType.Text,
                    "Deterministic Console Testcontainers investigation seeded from live Honua Server APIs."));
            },
            cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(connection, transaction,
            "DELETE FROM honua.investigation_pins WHERE investigation_id = @investigation_id",
            command => command.Parameters.Add(Parameter("investigation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.InvestigationId)),
            cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction,
            "DELETE FROM honua.investigation_links WHERE investigation_id = @investigation_id",
            command => command.Parameters.Add(Parameter("investigation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.InvestigationId)),
            cancellationToken).ConfigureAwait(false);

        var pinIds = new List<long>(capacity: 2)
        {
            await InsertPinAsync(
                connection,
                transaction,
                "alert:" + openAlertEventId.ToString(CultureInfo.InvariantCulture),
                OperateEventKind.Alert,
                seededAt.AddMinutes(-9),
                "Open harbor alert pinned by fixture.",
                seededAt.AddMinutes(-5),
                cancellationToken).ConfigureAwait(false),
            await InsertPinAsync(
                connection,
                transaction,
                "job:" + OperateObservabilityFixtureConstants.FailedJobId,
                OperateEventKind.Job,
                seededAt.AddMinutes(-8),
                "Failed artifact publishing job pinned by fixture.",
                seededAt.AddMinutes(-4),
                cancellationToken).ConfigureAwait(false)
        };

        var linkIds = new List<long>(capacity: 4)
        {
            await InsertLinkAsync(
                connection,
                transaction,
                InvestigationResourceKind.Alert,
                openAlertEventId.ToString(CultureInfo.InvariantCulture),
                "Open alert event.",
                seededAt.AddMinutes(-5),
                cancellationToken).ConfigureAwait(false),
            await InsertLinkAsync(
                connection,
                transaction,
                InvestigationResourceKind.Job,
                OperateObservabilityFixtureConstants.FailedJobId,
                "Failed durable job.",
                seededAt.AddMinutes(-4),
                cancellationToken).ConfigureAwait(false),
            await InsertLinkAsync(
                connection,
                transaction,
                InvestigationResourceKind.Release,
                FixtureReleaseId,
                "Synthetic fixture release.",
                seededAt.AddMinutes(-3),
                cancellationToken).ConfigureAwait(false),
            await InsertLinkAsync(
                connection,
                transaction,
                InvestigationResourceKind.ChangeSet,
                FixtureChangeSetId,
                "Synthetic fixture change-set.",
                seededAt.AddMinutes(-2),
                cancellationToken).ConfigureAwait(false)
        };

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new OperateObservabilityInvestigationFixtureSeed
        {
            InvestigationId = OperateObservabilityFixtureConstants.InvestigationId,
            PinIds = pinIds.ToArray(),
            LinkIds = linkIds.ToArray(),
            DetailLink = "/api/v1/admin/investigations/" + OperateObservabilityFixtureConstants.InvestigationId
        };
    }

    private static async Task<long> InsertPinAsync(
        DbConnection connection,
        DbTransaction transaction,
        string eventRef,
        OperateEventKind kind,
        DateTimeOffset occurredAt,
        string note,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO honua.investigation_pins (
                investigation_id, event_ref, event_kind, occurred_at, note, created_at, created_by)
            VALUES (
                @investigation_id, @event_ref, @event_kind, @occurred_at, @note, @created_at, @created_by)
            RETURNING pin_id
            """;
        command.Parameters.Add(Parameter("investigation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.InvestigationId));
        command.Parameters.Add(Parameter("event_ref", NpgsqlDbType.Text, eventRef));
        command.Parameters.Add(Parameter("event_kind", NpgsqlDbType.Smallint, (short)kind));
        command.Parameters.Add(Parameter("occurred_at", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(Parameter("note", NpgsqlDbType.Text, note));
        command.Parameters.Add(Parameter("created_at", NpgsqlDbType.TimestampTz, createdAt));
        command.Parameters.Add(Parameter("created_by", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.Actor));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Investigation pin insert did not return an id."));
    }

    private static async Task<long> InsertLinkAsync(
        DbConnection connection,
        DbTransaction transaction,
        InvestigationResourceKind kind,
        string resourceId,
        string note,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO honua.investigation_links (
                investigation_id, resource_kind, resource_id, note, created_at, created_by)
            VALUES (
                @investigation_id, @resource_kind, @resource_id, @note, @created_at, @created_by)
            RETURNING link_id
            """;
        command.Parameters.Add(Parameter("investigation_id", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.InvestigationId));
        command.Parameters.Add(Parameter("resource_kind", NpgsqlDbType.Smallint, (short)kind));
        command.Parameters.Add(Parameter("resource_id", NpgsqlDbType.Text, resourceId));
        command.Parameters.Add(Parameter("note", NpgsqlDbType.Text, note));
        command.Parameters.Add(Parameter("created_at", NpgsqlDbType.TimestampTz, createdAt));
        command.Parameters.Add(Parameter("created_by", NpgsqlDbType.Text, OperateObservabilityFixtureConstants.Actor));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Investigation link insert did not return an id."));
    }

    private static async Task ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        Action<DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, string> BuildLinks()
        => new(StringComparer.Ordinal)
        {
            ["events"] = "/api/v1/admin/observability/events?correlationId=" +
                         Uri.EscapeDataString(OperateObservabilityFixtureConstants.CorrelationId),
            ["logs"] = "/api/v1/admin/observability/logs",
            ["alerts"] = "/api/v1/admin/observability/alerts?serviceId=" +
                          Uri.EscapeDataString(OperateObservabilityFixtureConstants.ServiceId),
            ["alertRules"] = "/api/v1/admin/alerts/rules?serviceId=" +
                             Uri.EscapeDataString(OperateObservabilityFixtureConstants.ServiceId),
            ["alertZones"] = "/api/v1/admin/alerts/zones?serviceId=" +
                             Uri.EscapeDataString(OperateObservabilityFixtureConstants.ServiceId),
            ["jobs"] = "/api/v1/admin/jobs?correlationId=" +
                       Uri.EscapeDataString(OperateObservabilityFixtureConstants.CorrelationId),
            ["investigation"] = "/api/v1/admin/investigations/" +
                                OperateObservabilityFixtureConstants.InvestigationId
        };

    private static string BuildAlertPayload(string profile, string state, string? incidentDuration)
        => incidentDuration is null
            ? $$"""{"fixtureProfile":"{{profile}}","sourceHydrated":true,"state":"{{state}}","correlationId":"{{OperateObservabilityFixtureConstants.CorrelationId}}","traceId":"{{OperateObservabilityFixtureConstants.TraceId}}","zoneName":"{{OperateObservabilityFixtureConstants.ZoneName}}"}"""
            : $$"""{"fixtureProfile":"{{profile}}","sourceHydrated":true,"state":"{{state}}","correlationId":"{{OperateObservabilityFixtureConstants.CorrelationId}}","traceId":"{{OperateObservabilityFixtureConstants.TraceId}}","zoneName":"{{OperateObservabilityFixtureConstants.ZoneName}}","incidentDuration":"{{incidentDuration}}"}""";

    private static string BuildAuditDetails(string profile, string resourceType, string resourceId)
        => $$"""{"fixtureProfile":"{{profile}}","sourceHydrated":true,"resourceType":"{{resourceType}}","resourceId":"{{resourceId}}","serviceId":"{{OperateObservabilityFixtureConstants.ServiceId}}","correlationId":"{{OperateObservabilityFixtureConstants.CorrelationId}}","traceId":"{{OperateObservabilityFixtureConstants.TraceId}}"}""";

    private static NpgsqlParameter Parameter(string name, NpgsqlDbType type, object? value)
        => new(name, type) { Value = value ?? DBNull.Value };

    [LoggerMessage(
        EventId = 120906,
        Level = LogLevel.Information,
        Message = "Seeded Operate observability fixture profile {Profile}: zone {ZoneId}, rule {RuleId}, jobs {JobCount}, investigation {InvestigationId}.")]
    private static partial void FixtureSeeded(
        ILogger logger,
        string profile,
        long zoneId,
        long ruleId,
        int jobCount,
        string investigationId);
}

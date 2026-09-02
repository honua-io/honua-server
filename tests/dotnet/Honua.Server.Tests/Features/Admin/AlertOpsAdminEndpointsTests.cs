// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for the alert dispatch self-healing ops endpoints (#2561):
/// dead-letter redrive and per-channel pause/resume. Rows are seeded through the
/// real alert stores against the shared <c>honua.alert_dispatch</c> outbox and
/// asserted row-specifically by event id.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AlertOpsAdminEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/dispatch/redrive")]
    public async Task RedriveDeadLetters_WithSeededDeadLetterBacklog_DrainsBacklogToPending()
    {
        var eventId = await SeedDispatchAsync(AlertChannelType.Webhook);
        await SetDispatchStateAsync(eventId, status: 4, attempts: 5);

        var response = await _client.PostAsync("/api/v1/admin/alerts/dispatch/redrive", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").GetProperty("redriven").GetInt32().Should().BeGreaterThanOrEqualTo(1);

        var (status, attempts, lastError) = await ReadDispatchStateAsync(eventId);
        status.Should().Be(0, "the dead-lettered row must return to pending");
        attempts.Should().Be(0, "the attempt counter must be reset");
        lastError.Should().BeNull("the previous failure must be cleared");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/dispatch/redrive")]
    public async Task RedriveDeadLetters_IsIdempotent_SecondCallRedrivesNothingNew()
    {
        var eventId = await SeedDispatchAsync(AlertChannelType.Webhook);
        await SetDispatchStateAsync(eventId, status: 4, attempts: 5);

        var first = await _client.PostAsync("/api/v1/admin/alerts/dispatch/redrive", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var (statusAfterFirst, _, _) = await ReadDispatchStateAsync(eventId);
        statusAfterFirst.Should().Be(0);

        var second = await _client.PostAsync("/api/v1/admin/alerts/dispatch/redrive", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var (statusAfterSecond, attempts, _) = await ReadDispatchStateAsync(eventId);
        statusAfterSecond.Should().Be(0, "an already-pending row must not be disturbed by a second redrive");
        attempts.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/pause")]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/resume")]
    public async Task PauseChannel_StopsDispatchClaims_ResumeRestoresThem()
    {
        // A rarely-used channel keeps the shared-outbox blast radius small.
        var channel = AlertChannelType.AzureEventHub;
        var eventId = await SeedDispatchAsync(channel);

        var pauseResponse = await _client.PostAsync($"/api/v1/admin/alerts/channels/{channel.ToExternalName()}/pause", content: null);
        pauseResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var store = _fixture.GetService<IAlertDispatchStore>();
        var pausedClaims = await store.ClaimPendingAsync(500, DateTimeOffset.UtcNow.AddMinutes(1));
        pausedClaims.Should().NotContain(
            item => item.EventId == eventId,
            "a paused channel's rows must not be claimed for dispatch");

        var resumeResponse = await _client.PostAsync($"/api/v1/admin/alerts/channels/{channel.ToExternalName()}/resume", content: null);
        resumeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resumedClaims = await store.ClaimPendingAsync(500, DateTimeOffset.UtcNow.AddMinutes(1));
        resumedClaims.Should().Contain(
            item => item.EventId == eventId,
            "resuming the channel must restore dispatch claims for its enqueued rows");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/alerts/channels")]
    public async Task ListChannelStates_AfterPause_ReportsPausedChannel()
    {
        var channel = AlertChannelType.MicrosoftTeams;
        var pauseResponse = await _client.PostAsync($"/api/v1/admin/alerts/channels/{channel.ToExternalName()}/pause", content: null);
        pauseResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        try
        {
            var response = await _client.GetAsync("/api/v1/admin/alerts/channels");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            var entries = document.RootElement.GetProperty("data").EnumerateArray().ToList();
            entries.Should().Contain(
                entry => entry.GetProperty("channel").GetString() == channel.ToExternalName()
                    && entry.GetProperty("paused").GetBoolean(),
                "the paused channel must be reported with paused=true");
        }
        finally
        {
            (await _client.PostAsync($"/api/v1/admin/alerts/channels/{channel.ToExternalName()}/resume", content: null))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/alerts/channels/{channel}/pause")]
    public async Task PauseChannel_UnknownChannel_Returns400()
    {
        var response = await _client.PostAsync("/api/v1/admin/alerts/channels/not-a-channel/pause", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/alerts/rules/{ruleId}")]
    public async Task DeleteRule_AfterDeliveredOrDeadLetterIncident_RetainsHistoricalEvidence()
    {
        var migrationSchema = await _fixture.Postgres.CreateIsolatedSchemaAsync(nameof(AlertOpsAdminEndpointsTests));
        var migration = await _fixture.Postgres.RunEmbeddedMigrationsUnderLockAsync(
            migrationSchema,
            _fixture.Postgres.ConnectionString,
            Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await _fixture.Postgres.DropSchemaAsync(migrationSchema);

        foreach (var dispatchStatus in new short[] { 0, 1, 2, 3, 4 })
        {
            var ruleId = await SeedActiveRuleAsync(dispatchStatus);
            var eventId = await SeedRuleIncidentAsync(ruleId, dispatchStatus);

            var acknowledge = await _client.PostAsJsonAsync(
                $"/api/v1/admin/observability/alerts/{eventId}/acknowledge",
                new { note = "operator acknowledged", correlationId = $"retention-{eventId}" });
            acknowledge.StatusCode.Should().Be(HttpStatusCode.OK);

            var resolve = await _client.PostAsJsonAsync(
                $"/api/v1/admin/observability/alerts/{eventId}/resolve",
                new { note = "operator resolved", correlationId = $"retention-{eventId}" });
            resolve.StatusCode.Should().Be(HttpStatusCode.OK);

            var beforeDelete = await ReadRetainedEvidenceAsync(eventId);
            beforeDelete.EventCount.Should().Be(1);
            beforeDelete.DispatchStatus.Should().Be(dispatchStatus);

            var delete = await _client.DeleteAsync($"/api/v1/admin/alerts/rules/{ruleId}");
            delete.StatusCode.Should().Be(HttpStatusCode.OK);

            var evidence = await ReadRetainedEvidenceAsync(eventId);
            evidence.EventCount.Should().Be(1);
            evidence.RuleId.Should().BeNull("the mutable rule reference is detached, not cascaded");
            evidence.DispatchCount.Should().Be(1);
            evidence.DispatchStatus.Should().Be(dispatchStatus);
            evidence.LifecycleCount.Should().Be(1);
            evidence.LifecycleStatus.Should().Be(3, "the resolution remains queryable");
            evidence.Actor.Should().Be("admin");
            evidence.AuditCount.Should().BeGreaterThanOrEqualTo(2, "acknowledge and resolve domain audit actions survive");

            var dispatchStore = _fixture.GetService<IAlertDispatchStore>();
            var claimed = await dispatchStore.ClaimPendingAsync(5000, DateTimeOffset.UtcNow.AddMinutes(6));
            claimed.Should().NotContain(item => item.EventId == eventId,
                "dispatches detached from deleted rules must not be delivered");

            if (dispatchStatus == 4)
            {
                _ = await dispatchStore.RedriveDeadLettersAsync(DateTimeOffset.UtcNow, 5000);
                (await ReadRetainedEvidenceAsync(eventId)).DispatchStatus.Should().Be(4,
                    "retained dead letters detached from deleted rules must not be redriven");
            }
        }
    }

    /// <summary>
    /// Appends an ops-source alert event (no rule reference) and enqueues one pending
    /// dispatch row for <paramref name="channel"/>. Returns the persisted event id.
    /// </summary>
    private async Task<long> SeedDispatchAsync(AlertChannelType channel)
    {
        var eventStore = _fixture.GetService<IAlertEventStore>();
        var dispatchStore = _fixture.GetService<IAlertDispatchStore>();

        var eventId = await eventStore.TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"ops:test-2561:{Guid.NewGuid():N}",
            RuleId = 0,
            ServiceId = "ops-action-tests",
            LayerId = 0,
            ObjectId = 0,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 0,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"test\":\"2561\"}",
            IncidentStatus = AlertIncidentStatus.Started,
            Source = AlertEventSources.Ops,
        });

        eventId.Should().NotBeNull();
        var persistedEventId = eventId.GetValueOrDefault();
        await dispatchStore.EnqueueAsync(persistedEventId, ImmutableArray.Create(channel));
        return persistedEventId;
    }

    private async Task<long> SeedRuleIncidentAsync(long ruleId, short dispatchStatus)
    {
        var eventStore = _fixture.GetService<IAlertEventStore>();
        var dispatchStore = _fixture.GetService<IAlertDispatchStore>();
        var eventId = await eventStore.TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"rule:{ruleId}:generation:1",
            RuleId = ruleId,
            ServiceId = "retention-service",
            LayerId = 7,
            ObjectId = 42,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 1,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"value\":51}",
            IncidentStatus = AlertIncidentStatus.Started,
        });
        eventId.Should().NotBeNull();
        var persistedEventId = eventId.GetValueOrDefault();
        await dispatchStore.EnqueueAsync(persistedEventId, [AlertChannelType.Webhook]);

        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        var claimToken = Guid.NewGuid();
        await using var command = new NpgsqlCommand("""
            UPDATE honua.alert_dispatch
            SET status = 1,
                claim_token = @claim_token,
                updated_at = now()
            WHERE event_id = @event_id AND @status <> 0;

            UPDATE honua.alert_dispatch
            SET status = @status,
                attempts = CASE WHEN @status = 4 THEN max_attempts ELSE 1 END,
                delivered_at = CASE WHEN @status = 2 THEN now() ELSE NULL END,
                last_error = CASE WHEN @status = 4 THEN 'provider exhausted' ELSE NULL END,
                claim_token = NULL,
                updated_at = now()
            WHERE event_id = @event_id AND @status IN (2, 3, 4);
            """, connection);
        command.Parameters.AddWithValue("status", dispatchStatus);
        command.Parameters.AddWithValue("event_id", persistedEventId);
        command.Parameters.AddWithValue("claim_token", claimToken);
        var expectedUpdates = dispatchStatus is 0 ? 0 : dispatchStatus is 1 ? 1 : 2;
        (await command.ExecuteNonQueryAsync()).Should().Be(expectedUpdates);
        return persistedEventId;
    }

    private async Task<long> SeedActiveRuleAsync(short dispatchStatus)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.alert_rules
                (service_id, layer_id, rule_name, trigger_type, conditions,
                 severity, edition_required, channels, is_active)
            VALUES
                (@service_id, 7, @rule_name, 4,
                 '{"field":"speed","operator":">","value":50}'::jsonb,
                 'warning', 1, ARRAY['webhook'], TRUE)
            RETURNING rule_id;
            """, connection);
        command.Parameters.AddWithValue("service_id", $"retention-{dispatchStatus}-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("rule_name", $"retention-{dispatchStatus}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<RetainedEvidence> ReadRetainedEvidenceAsync(long eventId)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM honua.alert_events WHERE event_id = @event_id),
                (SELECT rule_id FROM honua.alert_events WHERE event_id = @event_id),
                (SELECT COUNT(*) FROM honua.alert_dispatch WHERE event_id = @event_id),
                (SELECT status FROM honua.alert_dispatch WHERE event_id = @event_id),
                (SELECT COUNT(*) FROM honua.alert_event_lifecycle WHERE event_id = @event_id),
                (SELECT lifecycle_status FROM honua.alert_event_lifecycle WHERE event_id = @event_id),
                (SELECT resolved_by FROM honua.alert_event_lifecycle WHERE event_id = @event_id),
                (SELECT COUNT(*) FROM honua.audit_log
                 WHERE resource_id = @resource_id AND action IN ('alert.acknowledge', 'alert.resolve'));
            """, connection);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("resource_id", eventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new RetainedEvidence(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetInt16(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetInt16(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt64(7));
    }

    private sealed record RetainedEvidence(
        long EventCount,
        long? RuleId,
        long DispatchCount,
        short? DispatchStatus,
        long LifecycleCount,
        short? LifecycleStatus,
        string? Actor,
        long AuditCount);

    private async Task SetDispatchStateAsync(long eventId, short status, int attempts)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "UPDATE honua.alert_dispatch SET status = @status, attempts = @attempts, last_error = 'seeded failure' WHERE event_id = @event_id",
            connection);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("attempts", attempts);
        command.Parameters.AddWithValue("event_id", eventId);
        (await command.ExecuteNonQueryAsync()).Should().BeGreaterThan(0);
    }

    private async Task<(short Status, int Attempts, string? LastError)> ReadDispatchStateAsync(long eventId)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, attempts, last_error FROM honua.alert_dispatch WHERE event_id = @event_id",
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the seeded dispatch row must exist");
        return (reader.GetInt16(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}

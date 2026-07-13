// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Integration tests for the payload-discriminated ops-action registry behind
/// <see cref="IAdminConfigChangeApplier"/> (#2561): every registered action executes
/// for real, is idempotent and audited, and unknown/malformed payloads fail closed
/// before any side effect.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
// Service-level integration tests over IAdminConfigChangeApplier (no HTTP surface of
// their own — the REST actuators are endpoint-proven in AlertOpsAdminEndpointsTests).
[Operation(Operations.TestInfrastructure)]
public sealed class OpsActionApplierTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private IAdminConfigChangeApplier Applier => _fixture.GetService<IAdminConfigChangeApplier>();

    [IntegrationTest]
    public async Task ApplyAsync_RedriveDeadLetters_DrainsSeededBacklogAndAudits()
    {
        var eventId = await SeedDispatchAsync(AlertChannelType.Webhook);
        await SetDispatchStateAsync(eventId, status: 4, attempts: 5);

        var changeId = await Applier.ApplyAsync(
            """{"action":"alerts.redrive_dead_letters","target":"alert-dispatch","params":{"maxCount":500}}""");

        changeId.Should().NotBeNullOrWhiteSpace();

        var (status, attempts) = await ReadDispatchStateAsync(eventId);
        status.Should().Be(0, "the dead-lettered row must be re-enqueued as pending");
        attempts.Should().Be(0, "the attempt counter must be reset");

        (await CountAuditRowsAsync("alerts.redrive_dead_letters", changeId!))
            .Should().Be(1, "the action must emit an audited operate-timeline event");
    }

    [IntegrationTest]
    public async Task ApplyAsync_PauseAndResumeChannel_TogglePersistedFlagAndClaims()
    {
        var channel = AlertChannelType.AwsSqs;
        var eventId = await SeedDispatchAsync(channel);
        var healthyEventId = await SeedDispatchAsync(AlertChannelType.Webhook);
        var store = _fixture.GetService<IAlertDispatchStore>();

        var pauseChangeId = await Applier.ApplyAsync(
            """{"action":"alerts.pause_channel","target":"aws_sqs","params":{"channel":"aws_sqs"}}""");
        pauseChangeId.Should().NotBeNullOrWhiteSpace();

        var pausedStates = await store.GetChannelPauseStatesAsync();
        pausedStates.Should().ContainKey(channel).WhoseValue.Should().BeTrue();

        var pausedClaims = await store.ClaimPendingAsync(500, DateTimeOffset.UtcNow.AddMinutes(1));
        pausedClaims.Should().NotContain(
            item => item.EventId == eventId,
            "a paused channel's rows must not be claimed");
        pausedClaims.Should().Contain(
            item => item.EventId == healthyEventId,
            "pausing one failing channel must not suppress healthy channel delivery");

        // Idempotent: pausing an already-paused channel succeeds and stays paused.
        (await Applier.ApplyAsync("""{"action":"alerts.pause_channel","params":{"channel":"aws_sqs"}}"""))
            .Should().NotBeNullOrWhiteSpace();

        var resumeChangeId = await Applier.ApplyAsync(
            """{"action":"alerts.resume_channel","params":{"channel":"aws_sqs"}}""");
        resumeChangeId.Should().NotBeNullOrWhiteSpace();

        var resumedStates = await store.GetChannelPauseStatesAsync();
        resumedStates.Should().ContainKey(channel).WhoseValue.Should().BeFalse();

        var resumedClaims = await store.ClaimPendingAsync(500, DateTimeOffset.UtcNow.AddMinutes(1));
        resumedClaims.Should().Contain(
            item => item.EventId == eventId,
            "resuming must restore dispatch claims");

        (await CountAuditRowsAsync("alerts.pause_channel", pauseChangeId!)).Should().Be(1);
        (await CountAuditRowsAsync("alerts.resume_channel", resumeChangeId!)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task ApplyAsync_TuneBoundedAdmission_RejectsOutOfRangeValues()
    {
        var gate = _fixture.GetService<IRuntimeTunableAdmissionGate>();
        var before = gate.CurrentLimit;

        var tooHighPayload = "{\"action\":\"db.tune_bounded_admission\",\"params\":{\"limit\":" + (gate.MaxLimit + 1) + "}}";
        var tooHigh = async () => await Applier.ApplyAsync(tooHighPayload);
        await tooHigh.Should().ThrowAsync<OpsActionException>();

        var tooLow = async () => await Applier.ApplyAsync(
            """{"action":"db.tune_bounded_admission","params":{"limit":0}}""");
        await tooLow.Should().ThrowAsync<OpsActionException>();

        var missingLimit = async () => await Applier.ApplyAsync(
            """{"action":"db.tune_bounded_admission","params":{}}""");
        await missingLimit.Should().ThrowAsync<OpsActionException>();

        gate.CurrentLimit.Should().Be(before, "a rejected tune must not change the admission target");
    }

    [IntegrationTest]
    public async Task ApplyAsync_TuneBoundedAdmission_AppliesInRangeValueTransiently()
    {
        var gate = _fixture.GetService<IRuntimeTunableAdmissionGate>();
        var original = gate.CurrentLimit;
        var target = Math.Max(gate.MinLimit, gate.MaxLimit - 1);

        try
        {
            var changeId = await Applier.ApplyAsync(
                "{\"action\":\"db.tune_bounded_admission\",\"target\":\"default\",\"params\":{\"limit\":" + target + "}}");

            changeId.Should().NotBeNullOrWhiteSpace();
            gate.CurrentLimit.Should().Be(target);
            (await CountAuditRowsAsync("db.tune_bounded_admission", changeId!)).Should().Be(1);
        }
        finally
        {
            // The tune is transient (in-memory only); restore the prior target so
            // later tests in the shared collection see the configured admission.
            gate.TrySetLimit(original, out _).Should().BeTrue();
        }
    }

    [IntegrationTest]
    public async Task ApplyAsync_UnknownOrMalformedPayloads_FailClosed()
    {
        var unknownAction = async () => await Applier.ApplyAsync(
            """{"action":"cache.warm_everything","params":{}}""");
        await unknownAction.Should().ThrowAsync<OpsActionException>("unknown actions must fail closed");

        var malformedJson = async () => await Applier.ApplyAsync("{not json");
        await malformedJson.Should().ThrowAsync<OpsActionException>("malformed payloads must fail closed");

        var missingAction = async () => await Applier.ApplyAsync("""{"target":"x"}""");
        await missingAction.Should().ThrowAsync<OpsActionException>("payloads without an action must fail closed");

        var emptyPayload = async () => await Applier.ApplyAsync(null);
        await emptyPayload.Should().ThrowAsync<OpsActionException>("empty payloads must fail closed");

        var invalidChannel = async () => await Applier.ApplyAsync(
            """{"action":"alerts.pause_channel","params":{"channel":"carrier-pigeon"}}""");
        await invalidChannel.Should().ThrowAsync<OpsActionException>("invalid parameters must fail closed");
    }

    private async Task<long> SeedDispatchAsync(AlertChannelType channel)
    {
        var eventStore = _fixture.GetService<IAlertEventStore>();
        var dispatchStore = _fixture.GetService<IAlertDispatchStore>();

        var eventId = await eventStore.TryAppendAsync(new AlertEventEnvelope
        {
            DedupeKey = $"ops:test-2561-applier:{Guid.NewGuid():N}",
            RuleId = 0,
            ServiceId = "ops-action-applier-tests",
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
        if (eventId is null)
        {
            throw new InvalidOperationException("TryAppendAsync failed to produce an event id.");
        }

        await dispatchStore.EnqueueAsync(eventId.Value, ImmutableArray.Create(channel));
        return eventId.Value;
    }

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

    private async Task<(short Status, int Attempts)> ReadDispatchStateAsync(long eventId)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, attempts FROM honua.alert_dispatch WHERE event_id = @event_id",
            connection);
        command.Parameters.AddWithValue("event_id", eventId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the seeded dispatch row must exist");
        return (reader.GetInt16(0), reader.GetInt32(1));
    }

    private async Task<long> CountAuditRowsAsync(string action, string correlationId)
    {
        var dataSource = _fixture.GetService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT COUNT(*) FROM honua.audit_log WHERE action = @action AND correlation_id = @correlation_id",
            connection);
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        var count = await command.ExecuteScalarAsync();
        return count is long value ? value : 0;
    }
}

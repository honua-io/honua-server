// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
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
        await dispatchStore.EnqueueAsync(eventId!.Value, ImmutableArray.Create(channel));
        return eventId!.Value;
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

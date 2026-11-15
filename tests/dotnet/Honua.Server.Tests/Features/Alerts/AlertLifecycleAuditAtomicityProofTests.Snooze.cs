// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

public sealed partial class AlertLifecycleAuditAtomicityProofTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    public async Task Suppress_CriticalAlert_RejectsWithoutLifecycleOrAuditIntent()
    {
        await SetCriticalSeverityAsync();
        using var response = await PostAsync("suppress", "critical-snooze");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Critical");
        (await ReadLifecycleStatusAsync()).Should().Be(0);
        (await ReadOutboxAsync(includeCompleted: true)).Should().BeEmpty();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/suppress")]
    public async Task Suppress_DeadlineBeyondSevenDays_RejectsWithoutLifecycleOrAuditIntent()
    {
        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/admin/observability/alerts/{_eventId}/suppress",
            new { suppressUntil = DateTimeOffset.UtcNow.AddDays(8) });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadLifecycleStatusAsync()).Should().Be(0);
        (await ReadOutboxAsync(includeCompleted: true)).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyAsync_CriticalSnooze_PreservesExistingAcknowledgementAndAudit()
    {
        using var acknowledged = await PostAsync("acknowledge", "critical-before-snooze");
        acknowledged.StatusCode.Should().Be(HttpStatusCode.OK);
        await SetCriticalSeverityAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var command = SnoozeCommand(DateTimeOffset.UtcNow);
        var transition = await store.ApplyAsync(command);
        transition.Lifecycle.Should().BeNull();
        (await ReadLifecycleStatusAsync()).Should().Be(1);
        (await ReadOutboxAsync(includeCompleted: true)).Should().ContainSingle()
            .Which.Action.Should().Be(AlertAuditActions.Acknowledge);
    }

    [IntegrationTheory]
    [Protocol(TestProtocols.Admin)]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/acknowledge")]
    [Endpoint("POST /api/v1/admin/observability/alerts/{eventId}/resolve")]
    [InlineData("acknowledge", 1)]
    [InlineData("resolve", 3)]
    public async Task Lifecycle_CriticalAlert_PreservesAcknowledgeAndResolve(string route, short status)
    {
        await SetCriticalSeverityAsync();
        using var response = await PostAsync(route, "critical-" + route);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadLifecycleStatusAsync()).Should().Be(status);
    }

    [IntegrationTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(0)]
    [InlineData(8)]
    public async Task SuppressAsync_InvalidDeadline_RejectsLegacyCaller(int days)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var now = DateTimeOffset.UtcNow;
        var lifecycle = await store.SuppressAsync(_eventId, "operator", now.AddDays(days), null, now);
        lifecycle.Should().BeNull();
        (await ReadLifecycleStatusAsync()).Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SuppressAsync_CriticalAlert_RejectsLegacyCaller()
    {
        await SetCriticalSeverityAsync();
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var now = DateTimeOffset.UtcNow;
        var lifecycle = await store.SuppressAsync(_eventId, "operator", now.AddHours(1), null, now);
        lifecycle.Should().BeNull();
        (await ReadLifecycleStatusAsync()).Should().Be(0);
    }

    [IntegrationTheory]
    [Operation(Operations.TestInfrastructure)]
    [InlineData(0)]
    [InlineData(8)]
    public async Task ApplyAsync_InvalidDeadline_RejectsWithoutAuditIntent(int days)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var now = DateTimeOffset.UtcNow;
        var transition = await store.ApplyAsync(SnoozeCommand(now) with { SuppressUntil = now.AddDays(days) });
        transition.Lifecycle.Should().BeNull();
        (await ReadLifecycleStatusAsync()).Should().Be(0);
        (await ReadOutboxAsync(includeCompleted: true)).Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ApplyAsync_SevenDayBoundaryAndExpiredRetry_ReplaysSingleIntent()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var now = DateTimeOffset.UtcNow;
        var command = SnoozeCommand(now) with { SuppressUntil = now.AddDays(7) };
        var applied = await store.ApplyAsync(command);
        applied.Lifecycle!.Status.Should().Be(AlertLifecycleStatus.Suppressed);
        var replay = await store.ApplyAsync(command with { OccurredAt = now.AddDays(9) });
        replay.Replayed.Should().BeTrue();
        replay.Intent!.OutboxId.Should().Be(applied.Intent!.OutboxId);
        (await ReadOutboxAsync(includeCompleted: true)).Should().ContainSingle();
    }

    private AlertLifecycleCommand SnoozeCommand(DateTimeOffset now) => new()
    {
        EventId = _eventId,
        Action = AlertLifecycleAction.Suppress,
        Actor = "operator",
        OccurredAt = now,
        SuppressUntil = now.AddHours(1),
        CorrelationId = "snooze-proof",
        IdempotencyKey = Guid.NewGuid().ToString("N")
    };

    private async Task SetCriticalSeverityAsync()
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""UPDATE "{_schema}".alert_events SET severity = 'critical' WHERE event_id = @event_id""",
            connection);
        command.Parameters.AddWithValue("event_id", _eventId);
        await command.ExecuteNonQueryAsync();
    }
}

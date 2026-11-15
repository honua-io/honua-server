// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

public sealed partial class AlertLifecycleAuditAtomicityProofTests
{
    [IntegrationTest]
    public async Task ApplyAsync_SnoozeWhileAcknowledgementWritesAudit_DoesNotDeadlock()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var scope = _fixture.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAlertLifecycleStore>();
        var snooze = SnoozeCommand(DateTimeOffset.UtcNow);
        await store.ApplyAsync(snooze with { Action = AlertLifecycleAction.Acknowledge }, timeout.Token);
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync(timeout.Token);
        await using var transaction = await connection.BeginTransactionAsync(timeout.Token);
        await using var update = new NpgsqlCommand(
            $"""UPDATE "{_schema}".alert_event_lifecycle SET note = 'concurrent acknowledgement' WHERE event_id = @event_id""",
            connection, transaction);
        update.Parameters.AddWithValue("event_id", _eventId);
        await update.ExecuteNonQueryAsync(timeout.Token);
        var pending = store.ApplyAsync(snooze with { IdempotencyKey = Guid.NewGuid().ToString("N") }, timeout.Token);
        await WaitForBlockedLifecycleAsync(connection.ProcessID, timeout.Token);
        await InsertConcurrentIntentAsync(transaction, timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        var result = await pending;
        result.Lifecycle!.Status.Should().Be(AlertLifecycleStatus.Suppressed);
        (await ReadOutboxAsync(includeCompleted: true)).Should().HaveCount(3);
    }

    private async Task WaitForBlockedLifecycleAsync(int blocker, CancellationToken cancellationToken)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync(cancellationToken);
        await using var query = new NpgsqlCommand(
            "SELECT EXISTS (SELECT FROM pg_stat_activity WHERE @blocker = ANY(pg_blocking_pids(pid)))", connection);
        query.Parameters.AddWithValue("blocker", blocker);
        while (await query.ExecuteScalarAsync(cancellationToken) is not true)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private async Task InsertConcurrentIntentAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".alert_audit_outbox
                (event_id, action, actor, correlation_id, idempotency_key, occurred_at)
            VALUES (@event_id, 'alert.acknowledge', 'operator', 'concurrent-ack', @key, now())
            """, transaction.Connection, transaction);
        insert.Parameters.AddWithValue("event_id", _eventId);
        insert.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

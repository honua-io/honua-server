// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Alerts.Abstractions;
using Honua.Core.Features.Alerts.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Alerts;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Alerts;

[Protocol(ProtocolNames.Infrastructure)]
[Operation(Operations.ContractTesting)]
[Collection("Database.Alerts")]
public sealed class AlertEvaluationAtomicityTests(DatabaseFixtureAdapter database)
{
    [IntegrationTest]
    public async Task CommitEvaluationAsync_FailureAtEveryBoundary_ReplayCommitsOneTransition()
    {
        var migrationSchema = await database.CreateIsolatedSchemaAsync(nameof(AlertEvaluationAtomicityTests));
        var migration = await database.RunEmbeddedMigrationsUnderLockAsync(
            migrationSchema,
            Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await database.DropSchemaAsync(migrationSchema);

        var state = new AlertStateSnapshot
        {
            RuleId = 3858,
            LayerId = 7,
            ObjectId = 42,
            Inside = true,
            LastGeneration = 19,
            ThresholdStateJson = "{\"matched\":true}"
        };
        var alertEvent = new AlertEventEnvelope
        {
            DedupeKey = "rule:3858:object:42:generation:19:threshold",
            RuleId = 3858,
            ServiceId = "atomic-alert-service",
            LayerId = 7,
            ObjectId = 42,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 19,
            Severity = AlertSeverity.Warning,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = "{\"value\":51}",
            IncidentStatus = AlertIncidentStatus.Started
        };
        var entry = new AlertOutboxEntry(alertEvent, [AlertChannelType.Webhook]);
        var provider = new TestConnectionProvider(database.DataSource);

        foreach (var boundary in Enum.GetValues<AlertEvaluationCommitBoundary>())
        {
            await ResetAsync();
            var faultedWriter = new PostgresAlertOutboxWriter(
                provider,
                reached =>
                {
                    if (reached == boundary)
                    {
                        throw new InjectedCommitFailureException(boundary);
                    }
                });

            var act = () => faultedWriter.CommitEvaluationAsync([state], [entry]);
            await act.Should().ThrowAsync<InjectedCommitFailureException>();
            (await SnapshotAsync()).Should().Be((0L, 0L, 0L, 0L));

            var restartedWriter = new PostgresAlertOutboxWriter(provider);
            (await restartedWriter.CommitEvaluationAsync([state], [entry])).Should().Equal(true);
            (await restartedWriter.CommitEvaluationAsync([state], [entry])).Should().Equal(false);
            (await SnapshotAsync()).Should().Be((1L, 1L, 1L, 19L));
        }
    }

    private async Task ResetAsync()
    {
        await database.ApplyGlobalSeedSqlAsync("""
            TRUNCATE TABLE honua.alert_event_lifecycle, honua.alert_dispatch, honua.alert_events,
                honua.alert_state, honua.alert_rules RESTART IDENTITY CASCADE;
            INSERT INTO honua.alert_rules
                (rule_id, service_id, layer_id, rule_name, trigger_type, conditions,
                 severity, edition_required, channels, is_active)
            VALUES
                (3858, 'atomic-alert-service', 7, 'atomic threshold', 2,
                 '{"field":"value","operator":">","value":50}'::jsonb,
                 'warning', 2, ARRAY['webhook'], true);
            """);
    }

    private async Task<(long States, long Events, long Dispatches, long Generation)> SnapshotAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM honua.alert_state),
                (SELECT COUNT(*) FROM honua.alert_events),
                (SELECT COUNT(*) FROM honua.alert_dispatch),
                COALESCE((SELECT MAX(last_generation) FROM honua.alert_state), 0);
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private sealed class InjectedCommitFailureException(AlertEvaluationCommitBoundary boundary)
        : Exception($"Injected failure at {boundary}.");

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => await dataSource.OpenConnectionAsync(cancellationToken);

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return (connection, transaction);
        }

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await operation();
        }
    }
}

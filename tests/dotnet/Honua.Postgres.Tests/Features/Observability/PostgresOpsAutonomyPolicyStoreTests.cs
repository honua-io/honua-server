// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Observability;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class PostgresOpsAutonomyPolicyStoreTests(PostgresFixture fixture)
{
    private const string Rule = "alert-dispatch-backlog";

    [IntegrationTest]
    public async Task PolicySettingsReservationAndOutcome_RoundTripThroughPostgres()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsAutonomyPolicyStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsAutonomyPolicyStore(
                new TestConnectionProvider(fixture.DataSource, schema),
                schemaName: schema);

            var snapshot = await store.SetPolicyAsync(
                new OpsAutonomyPolicy
                {
                    Rule = Rule,
                    Mode = OpsAutonomyMode.AutoApply,
                    MaxAutoActionsPerWindow = 1,
                    Window = TimeSpan.FromHours(1),
                    MaxBlastRadius = 3,
                },
                changedBy: "ops-admin",
                reason: "graduate after clean proposals");
            snapshot.Policy.Mode.Should().Be(OpsAutonomyMode.AutoApply);

            var storedPolicy = await store.GetPolicyAsync(Rule);
            storedPolicy.Should().NotBeNull();
            storedPolicy!.MaxBlastRadius.Should().Be(3);

            var settings = await store.SetSettingsAsync(
                new OpsAutonomySettings { KillSwitchEnabled = true },
                changedBy: "ops-admin");
            settings.KillSwitchEnabled.Should().BeTrue();
            (await store.GetSettingsAsync()).KillSwitchEnabled.Should().BeTrue();

            await store.SetSettingsAsync(new OpsAutonomySettings(), changedBy: "ops-admin");
            var reserved = await store.TryReserveAutoActionAsync(Reservation("finding-a"));
            reserved.Reserved.Should().BeTrue();
            reserved.ReservationId.Should().NotBeNullOrWhiteSpace();

            var duplicateFinding = await store.TryReserveAutoActionAsync(Reservation("finding-a"));
            duplicateFinding.Reserved.Should().BeFalse();
            duplicateFinding.Reason.Should().Be("finding-already-reserved");

            var rateLimited = await store.TryReserveAutoActionAsync(Reservation("finding-b"));
            rateLimited.Reserved.Should().BeFalse();
            rateLimited.Reason.Should().Be("rate-limit-exceeded");

            await store.RecordAutoActionOutcomeAsync(
                reserved.ReservationId!,
                OpsAutonomyActionOutcome.Succeeded,
                operationId: "op-1");
            await store.IncrementProposalRaisedAsync(Rule);

            var listed = await store.ListPoliciesAsync();
            listed.Should().ContainSingle();
            listed[0].TrackRecord.AutoApplied.Should().Be(1);
            listed[0].TrackRecord.ProposalsRaised.Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task RecordAutoActionOutcome_Indeterminate_PersistsAndCountsAsFailed()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsAutonomyPolicyStoreTests));
        try
        {
            await EnsureSchemaAsync(schema);
            var store = new PostgresOpsAutonomyPolicyStore(
                new TestConnectionProvider(fixture.DataSource, schema),
                schemaName: schema);
            await store.SetPolicyAsync(
                new OpsAutonomyPolicy { Rule = Rule },
                changedBy: "integration-test");
            var reserved = await store.TryReserveAutoActionAsync(Reservation("finding-indeterminate"));
            reserved.Reserved.Should().BeTrue();

            await store.RecordAutoActionOutcomeAsync(
                reserved.ReservationId!,
                OpsAutonomyActionOutcome.Indeterminate,
                operationId: "op-indeterminate",
                message: "verification and compensation did not establish convergence");

            var listed = await store.ListPoliciesAsync();
            listed.Should().ContainSingle();
            listed[0].TrackRecord.AutoApplied.Should().Be(0);
            listed[0].TrackRecord.RolledBack.Should().Be(0);
            listed[0].TrackRecord.Failed.Should().Be(1,
                "indeterminate outcomes must not improve the autonomous success record");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static OpsAutonomyReservationRequest Reservation(string findingId)
        => new()
        {
            Rule = Rule,
            FindingId = findingId,
            OperationClass = OperationClass.AdminConfigChange,
            ActionDiscriminator = "alerts.redrive_dead_letters",
            BlastRadius = 1,
            MaxAutoActionsPerWindow = 1,
            Window = TimeSpan.FromHours(1),
        };

    private async Task EnsureSchemaAsync(string schema)
    {
        await fixture.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS "{schema}".ops_autonomy_policies (
                rule                        TEXT        PRIMARY KEY,
                mode                        SMALLINT    NOT NULL DEFAULT 0,
                max_auto_actions_per_window INTEGER     NOT NULL DEFAULT 1,
                window_seconds              INTEGER     NOT NULL DEFAULT 3600,
                max_blast_radius            INTEGER     NOT NULL DEFAULT 1,
                updated_at                  TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_by                  TEXT        NOT NULL DEFAULT 'system',
                CONSTRAINT ops_autonomy_policies_valid_rule CHECK (length(rule) > 0),
                CONSTRAINT ops_autonomy_policies_valid_mode CHECK (mode IN (0, 1)),
                CONSTRAINT ops_autonomy_policies_valid_rate CHECK (max_auto_actions_per_window > 0),
                CONSTRAINT ops_autonomy_policies_valid_window CHECK (window_seconds > 0),
                CONSTRAINT ops_autonomy_policies_valid_blast CHECK (max_blast_radius > 0),
                CONSTRAINT ops_autonomy_policies_valid_actor CHECK (length(updated_by) > 0)
            );

            CREATE TABLE IF NOT EXISTS "{schema}".ops_autonomy_settings (
                settings_id         TEXT        PRIMARY KEY,
                kill_switch_enabled BOOLEAN     NOT NULL DEFAULT FALSE,
                updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_by          TEXT        NOT NULL DEFAULT 'system',
                CONSTRAINT ops_autonomy_settings_valid_id CHECK (length(settings_id) > 0),
                CONSTRAINT ops_autonomy_settings_valid_actor CHECK (length(updated_by) > 0)
            );

            CREATE TABLE IF NOT EXISTS "{schema}".ops_autonomy_rule_track_records (
                rule               TEXT        PRIMARY KEY,
                proposals_raised   BIGINT      NOT NULL DEFAULT 0,
                proposals_approved BIGINT      NOT NULL DEFAULT 0,
                proposals_rejected BIGINT      NOT NULL DEFAULT 0,
                auto_applied       BIGINT      NOT NULL DEFAULT 0,
                rolled_back        BIGINT      NOT NULL DEFAULT 0,
                failed             BIGINT      NOT NULL DEFAULT 0,
                first_activity_at  TIMESTAMPTZ NULL,
                last_activity_at   TIMESTAMPTZ NULL,
                CONSTRAINT ops_autonomy_track_valid_rule CHECK (length(rule) > 0),
                CONSTRAINT ops_autonomy_track_nonnegative_proposed CHECK (proposals_raised >= 0),
                CONSTRAINT ops_autonomy_track_nonnegative_approved CHECK (proposals_approved >= 0),
                CONSTRAINT ops_autonomy_track_nonnegative_rejected CHECK (proposals_rejected >= 0),
                CONSTRAINT ops_autonomy_track_nonnegative_auto CHECK (auto_applied >= 0),
                CONSTRAINT ops_autonomy_track_nonnegative_rollback CHECK (rolled_back >= 0),
                CONSTRAINT ops_autonomy_track_nonnegative_failed CHECK (failed >= 0)
            );

            CREATE TABLE IF NOT EXISTS "{schema}".ops_autonomy_action_log (
                action_id              TEXT        PRIMARY KEY,
                finding_id             TEXT        NOT NULL,
                rule                   TEXT        NOT NULL,
                operation_class        TEXT        NOT NULL,
                action_discriminator   TEXT        NULL,
                blast_radius           INTEGER     NOT NULL DEFAULT 1,
                reserved_at            TIMESTAMPTZ NOT NULL DEFAULT now(),
                outcome                SMALLINT    NULL,
                execution_operation_id TEXT        NULL,
                outcome_message        TEXT        NULL,
                completed_at           TIMESTAMPTZ NULL,
                CONSTRAINT ops_autonomy_action_valid_action CHECK (length(action_id) > 0),
                CONSTRAINT ops_autonomy_action_valid_finding CHECK (length(finding_id) > 0),
                CONSTRAINT ops_autonomy_action_valid_rule CHECK (length(rule) > 0),
                CONSTRAINT ops_autonomy_action_valid_operation CHECK (length(operation_class) > 0),
                CONSTRAINT ops_autonomy_action_valid_blast CHECK (blast_radius > 0),
                CONSTRAINT ops_autonomy_action_valid_outcome CHECK (outcome IS NULL OR outcome IN (0, 1, 2, 3, 4)),
                CONSTRAINT ops_autonomy_action_unique_finding UNIQUE (finding_id)
            );

            CREATE INDEX IF NOT EXISTS idx_ops_autonomy_action_rule_window
                ON "{schema}".ops_autonomy_action_log(rule, reserved_at DESC);

            CREATE INDEX IF NOT EXISTS idx_ops_autonomy_action_finding
                ON "{schema}".ops_autonomy_action_log(finding_id);
            """);
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schema) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var conn = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SET search_path TO \"{schema}\", public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return conn;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (conn, tx);
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
            => operation();
    }
}

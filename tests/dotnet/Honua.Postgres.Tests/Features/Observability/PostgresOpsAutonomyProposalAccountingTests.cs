// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Honua.Postgres.Features.Observability;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Observability;

[Collection("Database")]
public sealed class PostgresOpsAutonomyProposalAccountingTests(PostgresFixture fixture)
{
    private const string Rule = "alert-dispatch-backlog";

    [IntegrationTest]
    public async Task RecordProposalResolution_ConcurrentMultiReplicaRetries_IncrementsExactlyOnce()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresOpsAutonomyProposalAccountingTests));
        try
        {
            await fixture.ExecuteAsync($"""
                CREATE TABLE "{schema}".ops_autonomy_rule_track_records (
                    rule               TEXT        PRIMARY KEY,
                    proposals_raised   BIGINT      NOT NULL DEFAULT 0,
                    proposals_approved BIGINT      NOT NULL DEFAULT 0,
                    proposals_rejected BIGINT      NOT NULL DEFAULT 0,
                    auto_applied       BIGINT      NOT NULL DEFAULT 0,
                    rolled_back        BIGINT      NOT NULL DEFAULT 0,
                    failed             BIGINT      NOT NULL DEFAULT 0,
                    first_activity_at  TIMESTAMPTZ NULL,
                    last_activity_at   TIMESTAMPTZ NULL
                );
                CREATE TABLE "{schema}".ops_autonomy_proposal_resolutions (
                    proposal_id TEXT        PRIMARY KEY,
                    rule        TEXT        NOT NULL,
                    resolution  SMALLINT    NOT NULL,
                    resolved_at TIMESTAMPTZ NOT NULL
                );
                """);
            var stores = Enumerable.Range(0, 4)
                .Select(_ => new PostgresOpsAutonomyPolicyStore(
                    new TestConnectionProvider(fixture.DataSource, schema),
                    schemaName: schema))
                .ToArray();

            await Task.WhenAll(Enumerable.Range(0, 32).Select(index =>
                stores[index % stores.Length].RecordProposalResolutionAsync(
                    Rule,
                    "proposal-1",
                    OpsAutonomyProposalResolution.Approved)));
            await stores[0].RecordProposalResolutionAsync(
                Rule,
                "proposal-1",
                OpsAutonomyProposalResolution.Rejected);
            await stores[1].RecordProposalResolutionAsync(
                Rule,
                "proposal-2",
                OpsAutonomyProposalResolution.Rejected);

            await using var connection = await fixture.GetConnectionAsync(schema);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT proposals_approved, proposals_rejected
                FROM ops_autonomy_rule_track_records
                WHERE rule = @rule
                """;
            command.Parameters.AddWithValue("rule", Rule);
            await using var reader = await command.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetInt64(0).Should().Be(1);
            reader.GetInt64(1).Should().Be(1);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schema) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schema}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => operation();
    }
}

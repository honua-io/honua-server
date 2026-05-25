// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Share.Domain;
using Honua.Postgres.Features.Share;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Postgres.Tests.Features.Share;

[Collection("Database")]
public sealed class PostgresShareExportStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public async Task CursorPagination_WithSubMillisecondTimestamps_ReturnsNextDefinitionAndRun()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresShareExportStoreTests));
        try
        {
            await EnsureShareTablesAsync(schema);
            var store = new PostgresShareExportStore(new TestConnectionProvider(fixture.DataSource, schema), schema);
            var baseTime = DateTimeOffset.Parse("2026-05-25T03:00:00Z", CultureInfo.InvariantCulture);
            var older = baseTime.AddTicks(8000);
            var newer = baseTime.AddTicks(9000);
            var olderDefinition = BuildDefinition("postgres-cursor-definition-a", "postgres-cursor-layer", older);
            var newerDefinition = BuildDefinition("postgres-cursor-definition-b", "postgres-cursor-layer", newer);
            await store.CreateDefinitionAsync(olderDefinition);
            await store.CreateDefinitionAsync(newerDefinition);

            var firstDefinitions = await store.ListDefinitionsAsync(new ShareExportDefinitionQuery
            {
                ServiceName = "postgres-cursor-layer",
                Limit = 1
            });

            firstDefinitions.Items.Should().ContainSingle();
            firstDefinitions.Items[0].ExportId.Should().Be(newerDefinition.ExportId);
            firstDefinitions.NextCursor.Should().NotBeNullOrWhiteSpace();

            var nextDefinitions = await store.ListDefinitionsAsync(new ShareExportDefinitionQuery
            {
                ServiceName = "postgres-cursor-layer",
                Limit = 1,
                Cursor = firstDefinitions.NextCursor
            });

            nextDefinitions.Items.Should().ContainSingle();
            nextDefinitions.Items[0].ExportId.Should().Be(olderDefinition.ExportId);

            await store.AppendRunAsync(BuildRun(newerDefinition.ExportId, "postgres-cursor-run-a", older));
            await store.AppendRunAsync(BuildRun(newerDefinition.ExportId, "postgres-cursor-run-b", newer));

            var firstRuns = await store.ListRunsAsync(newerDefinition.ExportId, cursor: null, limit: 1);

            firstRuns.Items.Should().ContainSingle();
            firstRuns.Items[0].RunId.Should().Be("postgres-cursor-run-b");
            firstRuns.NextCursor.Should().NotBeNullOrWhiteSpace();

            var nextRuns = await store.ListRunsAsync(newerDefinition.ExportId, firstRuns.NextCursor, limit: 1);

            nextRuns.Items.Should().ContainSingle();
            nextRuns.Items[0].RunId.Should().Be("postgres-cursor-run-a");
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private async Task EnsureShareTablesAsync(string schema)
    {
        var root = FindRepoRoot();
        var migrationPath = Path.Combine(root, "src", "Honua.Server", "Migrations", "037_CreateShareExportTraffic.sql");
        var sql = await File.ReadAllTextAsync(migrationPath);
        sql = sql.Replace("honua.", $"\"{schema}\".", StringComparison.Ordinal);
        await fixture.ExecuteAsync(sql, schema);
    }

    private static ShareExportDefinition BuildDefinition(string exportId, string serviceName, DateTimeOffset timestamp)
        => new()
        {
            ExportId = exportId,
            ResourceId = $"content-{serviceName}",
            ServiceName = serviceName,
            LayerId = 7,
            DisplayName = $"{serviceName} export",
            DestinationType = ShareExportDestinationType.Webhook,
            DestinationStatus = ShareExportDestinationStatus.Supported,
            DestinationConfig = new Dictionary<string, string>
            {
                ["url"] = "https://example.invalid/share-webhook",
                ["credentialRef"] = "vault://share/webhook"
            },
            Format = "GeoJSON",
            Schedule = "0 * * * *",
            ScheduleState = ShareExportScheduleState.Active,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };

    private static ShareExportRun BuildRun(string exportId, string runId, DateTimeOffset triggeredAt)
        => new()
        {
            RunId = runId,
            ExportId = exportId,
            TriggerKind = ShareExportTriggerKind.Manual,
            Status = ShareExportRunStatus.Queued,
            JobRunId = null,
            TriggeredAt = triggeredAt,
            StartedAt = null,
            CompletedAt = null,
            TargetSummary = "Webhook https://example.invalid/share-webhook",
            ResultArtifacts = Array.Empty<string>(),
            LastError = null
        };

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "Honua.Server", "Migrations", "037_CreateShareExportTraffic.sql")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private sealed class TestConnectionProvider(NpgsqlDataSource dataSource, string schemaName) : IDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
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

        public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            return await operation().ConfigureAwait(false);
        }

        public async Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            await operation().ConfigureAwait(false);
        }
    }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using DbUp;
using DbUp.Helpers;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Collaboration.Operations;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using TestOperations = Honua.TestKit.Constants.Operations;

namespace Honua.Postgres.Tests.Features.Collaboration;

[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
public sealed class PostgresSavedMapOperationLogRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset _acceptedAt = new(2026, 8, 2, 20, 0, 0, TimeSpan.Zero);

    [IntegrationTest]
    [Operation(TestOperations.Query)]
    public async Task ReplayPendingCheckpointAsync_AfterRepositoryRestart_ReplaysOnlyNewOperation()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresSavedMapOperationLogRepositoryTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var initial = CreateRepository(provider, schema);

            await initial.AppendAsync(CreateRequest("map-restart", "op-1", 0));
            await initial.AppendAsync(CreateRequest("map-restart", "op-2", 1));
            await initial.RecordCheckpointAsync(
                new SavedMapId("map-restart"),
                new SavedMapOperationCursor(2));

            var restarted = CreateRepository(provider, schema);
            var append = await restarted.AppendAsync(CreateRequest("map-restart", "op-3", 2));
            var replay = await restarted.ReplayPendingCheckpointAsync(new SavedMapId("map-restart"));

            Assert.True(restarted.SupportsReplicaSharedReplay);
            Assert.True(restarted.SupportsRestartDurableReplay);
            Assert.True(restarted.SupportsRestartDurableCheckpointCursors);
            Assert.True(restarted.SupportsRestartDurableCheckpointing);
            Assert.Equal(3, append.HeadCursor.Value);
            Assert.Equal(SavedMapOperationReplayStatus.Ok, replay.Status);
            Assert.Equal(2, replay.SinceCursor.Value);
            Assert.Equal("op-3", Assert.Single(replay.Operations).OperationId.Value);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    [Operation(TestOperations.Query)]
    public async Task ReplayPendingCheckpointAsync_AfterRestartAndPrefixPrune_RemainsReplayable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresSavedMapOperationLogRepositoryTests));
        try
        {
            await ApplyMigrationAsync(schema);
            var provider = new TestConnectionProvider(fixture.DataSource, schema);
            var initial = CreateRepository(provider, schema, retainedOperationCount: 2);

            await initial.AppendAsync(CreateRequest("map-prune", "op-1", 0));
            await initial.RecordCheckpointAsync(
                new SavedMapId("map-prune"),
                new SavedMapOperationCursor(1));
            await initial.AppendAsync(CreateRequest("map-prune", "op-2", 1));
            await initial.AppendAsync(CreateRequest("map-prune", "op-3", 2));

            var restarted = CreateRepository(provider, schema, retainedOperationCount: 2);
            var replay = await restarted.ReplayPendingCheckpointAsync(new SavedMapId("map-prune"));

            Assert.Equal(SavedMapOperationReplayStatus.Ok, replay.Status);
            Assert.Equal(1, replay.SinceCursor.Value);
            Assert.Equal(1, replay.MinimumReplayCursor.Value);
            Assert.Equal(
                ["op-2", "op-3"],
                replay.Operations.Select(operation => operation.OperationId.Value).ToArray());
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static PostgresSavedMapOperationLogRepository CreateRepository(
        IAdoNetDatabaseConnectionProvider provider,
        string schema,
        int retainedOperationCount = 512) =>
        new(
            provider,
            new MvpSavedMapOperationConflictPolicy(),
            new FixedTimeProvider(_acceptedAt),
            retainedOperationCount,
            schema);

    private static SavedMapOperationAppendRequest CreateRequest(
        string mapId,
        string operationId,
        long baseCursor) => new()
        {
            OperationId = new SavedMapOperationId(operationId),
            MapId = new SavedMapId(mapId),
            ActorId = new SavedMapActorId("actor-1"),
            BaseCursor = new SavedMapOperationCursor(baseCursor),
            Kind = SavedMapOperationKind.SetViewport,
            Payload = JsonSerializer.SerializeToElement(new { operationId }),
        };

    private async Task ApplyMigrationAsync(string schema)
    {
        var repositoryRoot = FindRepositoryRoot();
        var migrationPath = Path.Join(
            repositoryRoot,
            "src",
            "Honua.Server",
            "Migrations",
            "092_CreateSavedMapOperationLog.sql");
        var migration = await File.ReadAllTextAsync(migrationPath);
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(fixture.DataSource.ConnectionString)
            .JournalTo(new NullJournal())
            .WithScript("092_CreateSavedMapOperationLog.sql", migration)
            .WithVariable("HonuaSchema", SchemaSearchPath.ValidateAndQuote(schema))
            .WithTransaction()
            .Build();
        var result = upgrader.PerformUpgrade();

        Assert.True(result.Successful, $"migration should complete successfully: {result.Error}");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located.");
    }

    private sealed class TestConnectionProvider(
        NpgsqlDataSource dataSource,
        string schemaName) : IAdoNetDatabaseConnectionProvider
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

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

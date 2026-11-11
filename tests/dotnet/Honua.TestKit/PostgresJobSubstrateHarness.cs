// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.TestKit;

// The PostgreSQL job substrate in a schema of its own: the job substrate migration applied, and
// the job store, queue and log store built on it.
internal sealed class PostgresJobSubstrateHarness : IAsyncDisposable
{
    private const string MigrationResourceName = "Honua.Server.Migrations.147_CreateJobSubstrate.sql";

    private readonly IIsolatedSchemaFixture _fixture;
    private readonly IOptions<JobStoreOptions> _options;

    private PostgresJobSubstrateHarness(IIsolatedSchemaFixture fixture, NpgsqlDataSource dataSource, string schema, JobStoreOptions options)
    {
        _fixture = fixture;
        DataSource = dataSource;
        Schema = schema;
        Database = new JobStoreDatabase(dataSource, schema);
        _options = Options.Create(options);
        JobStore = CreateNodeJobStore();
        Queue = new PostgresJobQueue(Database, NullLogger<PostgresJobQueue>.Instance);
        LogStore = new PostgresExecutionLogStore(Database, _options, NullLogger<PostgresExecutionLogStore>.Instance);
    }

    public NpgsqlDataSource DataSource { get; }

    public string Schema { get; }

    public JobStoreDatabase Database { get; }

    public PostgresExecutionJobStore JobStore { get; }

    public PostgresJobQueue Queue { get; }

    public PostgresExecutionLogStore LogStore { get; }

    public string JobsTable => Database.Table(ExecutionJobRow.TableName);

    public static Task<PostgresJobSubstrateHarness> CreateAsync(IIsolatedSchemaFixture fixture, string testClassName)
        => CreateAsync(fixture, fixture.DataSource, testClassName);

    // Builds the substrate over dataSource instead of the fixture's pool, for
    // tests that count or break their own connections.
    public static async Task<PostgresJobSubstrateHarness> CreateAsync(
        IIsolatedSchemaFixture fixture,
        NpgsqlDataSource dataSource,
        string testClassName)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(testClassName);
        await fixture.ExecuteDdlUnderLockAsync(ReadMigration().Replace("$HonuaSchema$", $"\"{schema}\"", StringComparison.Ordinal));
        return new PostgresJobSubstrateHarness(fixture, dataSource, schema, new JobStoreOptions());
    }

    // A job store instance of its own over this schema, as each server node has.
    public PostgresExecutionJobStore CreateNodeJobStore()
        => new(Database, _options, NullLogger<PostgresExecutionJobStore>.Instance);

    public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    // Moves a claimed job's claim and heartbeat into the past on the database clock, which is
    // how a worker that stopped heartbeating looks to the reconciler.
    public Task AgeClaimAsync(string operationId, TimeSpan age)
        => ExecuteAsync(
            $"UPDATE {JobsTable} SET claimed_at = now() - @age, last_heartbeat_at = now() - @age WHERE operation_id = @operation_id",
            new NpgsqlParameter("age", age),
            new NpgsqlParameter("operation_id", operationId));

    public Task<bool> IsInQueueAsync(string operationId)
        => ScalarAsync<bool>(
            $"SELECT queue_visible_at IS NOT NULL FROM {JobsTable} WHERE operation_id = @operation_id",
            new NpgsqlParameter("operation_id", operationId));

    public JobExecutionService CreateWorker(
        IJobExecutor executor,
        IJobTerminalCallback terminalCallback,
        IExecutionJobStore? jobStore = null)
        => new(
            Queue,
            jobStore ?? JobStore,
            [executor],
            new ExecutionJobCancellationTokens(),
            [terminalCallback],
            LogStore,
            NullLogger<JobExecutionService>.Instance);

    // Runs one reconciliation sweep with its own cancellation-token registry, as a reconciler in
    // another process than the worker has.
    public async Task RunReconciliationSweepAsync(CancellationToken cancellationToken)
    {
        using var service = new JobReconciliationService(
            JobStore,
            Queue,
            Queue,
            new ExecutionJobCancellationTokens(),
            Array.Empty<IJobTerminalCallback>(),
            LogStore,
            NullLogger<JobReconciliationService>.Instance,
            JobStore,
            JobStore);

        await service.SweepActiveJobsAsync(cancellationToken);
    }

    public async Task<ExecutionJobRecord> WaitForJobAsync(
        string operationId,
        Func<ExecutionJobRecord, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        ExecutionJobRecord? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await JobStore.GetAsync(operationId);
            if (last != null && predicate(last))
            {
                return last;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            $"Timed out waiting for job '{operationId}'. Last observed: " +
            (last == null ? "<deleted>" : $"{last.Status}/attempt {last.AttemptCount}/owner {last.ClaimedBy}"));
    }

    public async ValueTask DisposeAsync()
        => await _fixture.DropSchemaAsync(Schema);

    private static string ReadMigration()
    {
        using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(MigrationResourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{MigrationResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

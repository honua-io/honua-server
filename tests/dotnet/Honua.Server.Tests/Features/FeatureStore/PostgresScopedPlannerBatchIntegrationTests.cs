// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Transactions;
using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Features.FeatureStore;

/// <summary>
/// Wire-level lifecycle and plan-isolation contract for the scoped planner
/// batch. These exercise ordinary pooled connections, including
/// the NoResetOnClose setting used by the production data source factory.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Query)]
public sealed class PostgresScopedPlannerBatchIntegrationTests(DatabaseFixtureAdapter fixture, ITestOutputHelper output)
{
    private const string LocalPlannerSetting = "SELECT set_config('max_parallel_workers_per_gather', '0', true)";

    [IntegrationTheory]
    [InlineData("unprepared")]
    [InlineData("prepared")]
    [InlineData("auto-prepared")]
    public async Task Batch_Success_RestoresSettingOnSameConnectionAndPooledReuse(string preparation)
    {
        await using var source = CreateDataSource(autoPrepare: preparation == "auto-prepared");
        int processId;
        await using (var connection = await source.OpenConnectionAsync())
        {
            processId = connection.ProcessID;
            await ExecuteAsync(connection, "SET max_parallel_workers_per_gather = 2");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await using var batch = CreateBatch(connection,
                    "SELECT $1::integer, current_setting('max_parallel_workers_per_gather')");
                batch.BatchCommands[1].Parameters.Add(new NpgsqlParameter { Value = 101 });
                if (preparation == "prepared")
                {
                    await batch.PrepareAsync();
                }

                await using (var reader = await batch.ExecuteReaderAsync())
                {
                    (await reader.ReadAsync()).Should().BeTrue();
                    reader.GetString(0).Should().Be("0");
                    (await reader.NextResultAsync()).Should().BeTrue();
                    (await reader.ReadAsync()).Should().BeTrue();
                    reader.GetInt32(0).Should().Be(101);
                    reader.GetString(1).Should().Be("0");
                    (await reader.NextResultAsync()).Should().BeFalse();
                }

                if (preparation == "prepared" || preparation == "auto-prepared" && attempt == 2)
                {
                    await using var prepared = new NpgsqlCommand(
                        "SELECT count(*) FROM pg_prepared_statements WHERE statement = $1", connection);
                    prepared.Parameters.Add(new NpgsqlParameter { Value = batch.BatchCommands[1].CommandText });
                    ((long)(await prepared.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
                }

                (await ReadSettingAsync(connection)).Should().Be("2");
            }
        }

        await AssertPooledSettingAsync(source, processId);
    }

    [IntegrationTest]
    public async Task Batch_ConfiguredCommandTimeout_PreservesConnectionDefault()
    {
        await using var source = CreateDataSource(commandTimeoutSeconds: 9);
        await using var connection = await source.OpenConnectionAsync();
        await using var batch = CreateBatch(connection, "SELECT 1");
        batch.Timeout.Should().Be(9);
        await DrainAsync(batch);
    }

    [IntegrationTest]
    public async Task Batch_DisposeBeforeReadingSecondResult_RestoresSettingBeforePooledReuse()
    {
        await using var source = CreateDataSource();
        int processId;
        await using (var connection = await source.OpenConnectionAsync())
        {
            processId = connection.ProcessID;
            await ExecuteAsync(connection, "SET max_parallel_workers_per_gather = 2");
            await using var batch = CreateBatch(connection, "SELECT generate_series(1, 1000)");
            await using (var reader = await batch.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be("0");
                // Leave the SELECT unread, as a failed materializer or early
                // consumer exit might. Disposal must drain through batch Sync.
            }

            (await ReadSettingAsync(connection)).Should().Be("2");
        }

        await AssertPooledSettingAsync(source, processId);
    }

    [IntegrationTest]
    public async Task Batch_SqlError_RollsBackSettingBeforePooledReuse()
    {
        await using var source = CreateDataSource();
        int processId;
        await using (var connection = await source.OpenConnectionAsync())
        {
            processId = connection.ProcessID;
            await ExecuteAsync(connection, "SET max_parallel_workers_per_gather = 2");
            await using var batch = CreateBatch(connection, "SELECT 1 / $1::integer");
            batch.BatchCommands[1].Parameters.Add(new NpgsqlParameter { Value = 0 });
            var execute = async () => await DrainAsync(batch);
            (await execute.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("22012");
            (await ReadSettingAsync(connection)).Should().Be("2");
        }

        await AssertPooledSettingAsync(source, processId);
    }

    [IntegrationTest]
    public async Task Batch_CancelDuringSecondStatement_RollsBackSettingBeforePooledReuse()
    {
        await using var source = CreateDataSource();
        int processId;
        await using (var connection = await source.OpenConnectionAsync())
        {
            processId = connection.ProcessID;
            await ExecuteAsync(connection, "SET max_parallel_workers_per_gather = 2");
            await using var observer = await fixture.DataSource.OpenConnectionAsync();
            await using var batch = CreateBatch(connection, "SELECT pg_sleep(30)");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var execution = DrainAsync(batch, cancellation.Token);
            try
            {
                // Wait for evidence that the second statement is executing, so
                // this cannot pass by cancelling before set_config is reached.
                await WaitForSleepAsync(observer, processId);
            }
            finally
            {
                await cancellation.CancelAsync();
                try
                {
                    await execution;
                }
                catch (OperationCanceledException)
                {
                    // Observe completion before disposing the batch, including
                    // when the observer fails to see the expected wait event.
                }
            }

            var observe = async () => await execution;
            await observe.Should().ThrowAsync<OperationCanceledException>();
            (await ReadSettingAsync(connection)).Should().Be("2");
        }

        await AssertPooledSettingAsync(source, processId);
    }

    [IntegrationTest]
    public async Task Batch_ExplicitTransaction_LocalSettingOutlivesBatchUntilRollback()
    {
        await using var source = CreateDataSource();
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, "SET max_parallel_workers_per_gather = 2");
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var batch = CreateBatch(connection, "SELECT 1"))
        {
            batch.Transaction = transaction;
            await DrainAsync(batch);
        }

        // The proposed policy must skip caller-owned transactions: a batch
        // boundary cannot restore SET LOCAL inside an outer transaction.
        (await ReadSettingAsync(connection)).Should().Be("0");
        await transaction.RollbackAsync();
        (await ReadSettingAsync(connection)).Should().Be("2");
    }

    [IntegrationTest]
    public async Task Batch_AmbientTransaction_LocalSettingOutlivesBatchUntilScopeEnds()
    {
        await using var source = CreateDataSource();
        int processId;
        await using (var initial = await source.OpenConnectionAsync())
        {
            processId = initial.ProcessID;
            await ExecuteAsync(initial, "SET max_parallel_workers_per_gather = 2");
        }

        using (var scope = new TransactionScope(TransactionScopeOption.Required,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted },
            TransactionScopeAsyncFlowOption.Enabled))
        {
            await using var connection = await source.OpenConnectionAsync();
            connection.ProcessID.Should().Be(processId);
            await using var batch = CreateBatch(connection, "SELECT 1");
            await DrainAsync(batch);
            (await ReadSettingAsync(connection)).Should().Be("0");
            scope.Complete();
        }

        await AssertPooledSettingAsync(source, processId);
    }

    [IntegrationTest]
    public async Task Batch_BoundedSpatialSelect_ChangesActualPlanAndPreservesOrderedOutput()
    {
        await using var source = CreateDataSource();
        await using var connection = await source.OpenConnectionAsync();
        var table = await CreatePlanTableAsync(connection);
        try
        {
            await ConfigurePlanProbeAsync(connection);
            var sql = BuildSelect(table);
            var baseline = await ReadRowsAsync(connection, sql, scoped: false);
            var scoped = await ReadRowsAsync(connection, sql, scoped: true);
            scoped.Should().Equal(baseline);
            scoped.Should().HaveCount(101);

            var baselinePlan = await ExplainAsync(connection, sql, scoped: false);
            var scopedPlan = await ExplainAsync(connection, sql, scoped: true);
            output.WriteLine("Baseline EXPLAIN ANALYZE: " + baselinePlan);
            output.WriteLine("Scoped EXPLAIN ANALYZE: " + scopedPlan);
            HasParallelPlan(baselinePlan).Should().BeTrue("the control must actually select a parallel plan");
            HasParallelPlan(scopedPlan).Should().BeFalse();
            (await ReadSettingAsync(connection)).Should().Be("2");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    [IntegrationTest]
    public async Task Batch_PreexistingPreparedPlan_IsNotRetunedByTransactionLocalSetting()
    {
        await using var source = CreateDataSource();
        await using var connection = await source.OpenConnectionAsync();
        var table = await CreatePlanTableAsync(connection);
        try
        {
            await ConfigurePlanProbeAsync(connection);
            await ExecuteAsync(connection, "SET plan_cache_mode = force_generic_plan");
            await ExecuteAsync(connection, $"PREPARE planner_probe(integer) AS {BuildSelect(table)}");
            var baselinePlan = await ExplainAsync(connection, "EXECUTE planner_probe(101)", scoped: false, parameterized: false);
            var scopedPlan = await ExplainAsync(connection, "EXECUTE planner_probe(101)", scoped: true, parameterized: false);
            output.WriteLine("Previously prepared baseline: " + baselinePlan);
            output.WriteLine("Previously prepared scoped: " + scopedPlan);
            HasParallelPlan(baselinePlan).Should().BeTrue();
            // This characterizes the prepared-plan hazard. A production policy
            // needs a separate statement identity for its scoped SELECT so auto
            // preparation cannot reuse a plan made in the ordinary path.
            HasParallelPlan(scopedPlan).Should().BeTrue();
            (await ReadSettingAsync(connection)).Should().Be("2");
        }
        finally
        {
            await ExecuteAsync(connection, $"DEALLOCATE ALL; DROP TABLE IF EXISTS {table}");
        }
    }

    [IntegrationTest]
    public async Task Batch_PreparedBeforeExecution_CreatesPlanUnderLocalSetting()
    {
        await using var source = CreateDataSource();
        await using var connection = await source.OpenConnectionAsync();
        var table = await CreatePlanTableAsync(connection);
        try
        {
            await ConfigurePlanProbeAsync(connection);
            await ExecuteAsync(connection, "SET plan_cache_mode = force_generic_plan");
            var sql = BuildSelect(table);
            HasParallelPlan(await ExplainAsync(connection, sql, scoped: false)).Should().BeTrue();
            await using (var batch = CreateBatch(connection, sql))
            {
                batch.BatchCommands[1].Parameters.Add(new NpgsqlParameter { Value = 101 });
                await batch.PrepareAsync();
                await DrainAsync(batch);
            }

            (await ReadSettingAsync(connection)).Should().Be("2");
            await using var command = new NpgsqlCommand(
                "SELECT name FROM pg_prepared_statements WHERE statement = $1", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = sql });
            var statementName = (string)(await command.ExecuteScalarAsync())!;
            statementName.Should().NotBeNullOrEmpty();
            var escapedName = statementName.Replace("\"", "\"\"", StringComparison.Ordinal);
            var plan = await ExplainAsync(connection, $"EXECUTE \"{escapedName}\"(101)", scoped: false, parameterized: false);
            output.WriteLine("Plan first bound inside scoped batch, reused outside: " + plan);
            HasParallelPlan(plan).Should().BeFalse();
        }
        finally
        {
            await ExecuteAsync(connection, $"DEALLOCATE ALL; DROP TABLE IF EXISTS {table}");
        }
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_SelectAllIdentity_IsolatesAutoPreparedPlansOnSameBackend(bool multiplexing)
    {
        await using var source = CreateDataSource(autoPrepare: true, multiplexing: multiplexing);
        await using var connection = await source.OpenConnectionAsync();
        var table = await CreatePlanTableAsync(connection);
        try
        {
            await ConfigurePlanProbeAsync(connection);
            await ExecuteAsync(connection, "SET plan_cache_mode = force_generic_plan");
            var ordinarySql = BuildSelect(table);
            // ALL is SELECT's default modifier, so this changes only the
            // prepared-statement identity, not relational or ordering semantics.
            var scopedSql = "SELECT ALL" + ordinarySql["SELECT".Length..];
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var ordinary = await ReadRowsAsync(connection, ordinarySql, scoped: false);
                var scoped = await ReadRowsAsync(connection, scopedSql, scoped: true);
                scoped.Should().Equal(ordinary);
                scoped.Should().HaveCount(101);
                (await ReadSettingAsync(connection)).Should().Be("2");
            }

            var ordinaryPlan = await ExplainPreparedAsync(connection, ordinarySql);
            var scopedPlan = await ExplainPreparedAsync(connection, scopedSql);
            output.WriteLine("Auto-prepared ordinary SELECT: " + ordinaryPlan);
            output.WriteLine("Auto-prepared scoped SELECT ALL: " + scopedPlan);
            HasParallelPlan(ordinaryPlan).Should().BeTrue();
            HasParallelPlan(scopedPlan).Should().BeFalse();
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table}");
        }
    }

    private static async Task<string> ExplainPreparedAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(
            "SELECT name FROM pg_prepared_statements WHERE statement = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = sql });
        var name = (string)(await command.ExecuteScalarAsync())!;
        name.Should().NotBeNullOrEmpty();
        var escapedName = name.Replace("\"", "\"\"", StringComparison.Ordinal);
        return await ExplainAsync(connection, $"EXECUTE \"{escapedName}\"(101)", scoped: false, parameterized: false);
    }

    private NpgsqlDataSource CreateDataSource(bool autoPrepare = false, bool multiplexing = false, int commandTimeoutSeconds = 30)
    {
        var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Pooling = true,
            MaxPoolSize = 1,
            MinPoolSize = 0,
            NoResetOnClose = true,
            Multiplexing = multiplexing,
            Enlist = true,
            MaxAutoPrepare = autoPrepare ? 10 : 0,
            AutoPrepareMinUsages = 1,
            CommandTimeout = commandTimeoutSeconds
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    private static NpgsqlBatch CreateBatch(NpgsqlConnection connection, string sql)
    {
        var batch = new NpgsqlBatch(connection) { EnableErrorBarriers = false };
        batch.BatchCommands.Add(new NpgsqlBatchCommand(LocalPlannerSetting));
        batch.BatchCommands.Add(new NpgsqlBatchCommand(sql));
        return batch;
    }

    private static async Task DrainAsync(NpgsqlBatch batch, CancellationToken cancellationToken = default)
    {
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
            }
        } while (await reader.NextResultAsync(cancellationToken));
    }

    private static async Task AssertPooledSettingAsync(NpgsqlDataSource source, int processId)
    {
        await using var reused = await source.OpenConnectionAsync();
        reused.ProcessID.Should().Be(processId, "a new backend would hide retained session state");
        (await ReadSettingAsync(reused)).Should().Be("2");
    }

    private static async Task<string> ReadSettingAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SHOW max_parallel_workers_per_gather", connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForSleepAsync(NpgsqlConnection observer, int processId)
    {
        var elapsed = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand("SELECT wait_event FROM pg_stat_activity WHERE pid = $1", observer);
        command.Parameters.Add(new NpgsqlParameter { Value = processId });
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (await command.ExecuteScalarAsync() is "PgSleep")
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The cancellation probe did not reach its second statement.");
    }

    private static async Task<string> CreatePlanTableAsync(NpgsqlConnection connection)
    {
        var table = "public.planner_probe_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $$"""
            CREATE TABLE {{table}} (id bigint PRIMARY KEY, geom geometry(Point, 4326), label text);
            INSERT INTO {{table}}
            SELECT id, ST_SetSRID(ST_MakePoint((id % 360) - 180, ((id / 360) % 180) - 90), 4326), 'feature-' || id
            FROM generate_series(1, 100000) AS id;
            ANALYZE {{table}};
            """);
        return table;
    }

    private static Task ConfigurePlanProbeAsync(NpgsqlConnection connection) => ExecuteAsync(connection, """
        SET max_parallel_workers_per_gather = 2;
        SET min_parallel_table_scan_size = 0;
        SET parallel_setup_cost = 0;
        SET parallel_tuple_cost = 0;
        SET enable_indexscan = off;
        SET enable_bitmapscan = off;
        SET jit = off;
        """);

    // Deliberately controlled costs make this a deterministic mechanism test,
    // not evidence of a production speedup or a representative selectivity.
    private static string BuildSelect(string table) => $$"""
        SELECT id::bigint AS objectid, ST_AsBinary(geom::geometry) AS geometry,
               jsonb_build_object('id', id, 'label', label)::text AS attributes
        FROM {{table}}
        WHERE ST_Intersects(geom::geometry, ST_MakeEnvelope(-180, -90, 180, 90, 4326))
        ORDER BY id ASC LIMIT $1 OFFSET 0
        """;

    private static async Task<List<string>> ReadRowsAsync(NpgsqlConnection connection, string sql, bool scoped)
    {
        await using var batch = scoped ? CreateBatch(connection, sql) : new NpgsqlBatch(connection);
        if (!scoped)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand(sql));
        }

        batch.BatchCommands[^1].Parameters.Add(new NpgsqlParameter { Value = 101 });
        await using var reader = await batch.ExecuteReaderAsync();
        if (scoped)
        {
            (await reader.NextResultAsync()).Should().BeTrue();
        }

        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture) + ":" +
                Convert.ToHexString(reader.GetFieldValue<byte[]>(1)) + ":" + reader.GetString(2));
        }

        return rows;
    }

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, string sql, bool scoped, bool parameterized = true)
    {
        sql = "EXPLAIN (ANALYZE, FORMAT JSON, TIMING OFF) " + sql;
        await using var batch = scoped ? CreateBatch(connection, sql) : new NpgsqlBatch(connection);
        if (!scoped)
        {
            batch.BatchCommands.Add(new NpgsqlBatchCommand(sql));
        }

        if (parameterized)
        {
            batch.BatchCommands[^1].Parameters.Add(new NpgsqlParameter { Value = 101 });
        }

        await using var reader = await batch.ExecuteReaderAsync();
        if (scoped)
        {
            (await reader.NextResultAsync()).Should().BeTrue();
        }

        (await reader.ReadAsync()).Should().BeTrue();
        return reader.GetString(0);
    }

    private static bool HasParallelPlan(string json)
    {
        using var document = JsonDocument.Parse(json);
        return HasParallelNode(document.RootElement[0].GetProperty("Plan"));
    }

    private static bool HasParallelNode(JsonElement node) =>
        node.GetProperty("Node Type").GetString() is "Gather" or "Gather Merge" ||
        node.TryGetProperty("Plans", out var children) && children.EnumerateArray().Any(HasParallelNode);
}

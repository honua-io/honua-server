// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

[Collection("Database")]
public sealed class PostgresGenerationFenceProtocolTests(PostgresFixture fixture)
{
    [IntegrationTheory(Timeout = 180000)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("rollback", false)]
    [InlineData("rollback", true)]
    [InlineData("savepoint-commit", false)]
    [InlineData("savepoint-commit", true)]
    [InlineData("savepoint-rollback", false)]
    [InlineData("savepoint-rollback", true)]
    public async Task TransactionFence_AssignedLowerWriter_DefersUntilItsTopTransactionFinishes(
        string outcome, bool higherXidFirst)
    {
        await using var scope = await FenceScope.CreateAsync(fixture);
        await using var lower = await scope.OpenAsync();
        await using var higher = await scope.OpenAsync();
        await using var publisher = await scope.OpenAsync();
        await using var higherTransaction = await higher.BeginTransactionAsync();
        if (higherXidFirst)
        {
            await ScalarAsync(higher, "SELECT pg_current_xact_id()::text");
        }

        await using var transaction = await lower.BeginTransactionAsync();
        if (outcome.StartsWith("savepoint", StringComparison.Ordinal))
        {
            await transaction.SaveAsync("edit");
        }

        await ScalarAsync(lower, "SELECT pg_current_xact_id()::text");
        var lowerGeneration = await AllocateAsync(lower);
        await ExecuteAsync(lower, $"INSERT INTO changes VALUES ({lowerGeneration})");
        await ScalarAsync(higher, "SELECT pg_current_xact_id()::text");
        var higherGeneration = await AllocateAsync(higher);
        await ExecuteAsync(higher, $"INSERT INTO changes VALUES ({higherGeneration})");
        await higherTransaction.CommitAsync();
        var candidate = await CaptureAsync(publisher);

        (await FenceOpenAsync(publisher, candidate)).Should().BeFalse();
        (await ScalarAsync(publisher, "SELECT MAX(generation) FROM changes"))
            .Should().Be(higherGeneration);

        if (outcome == "savepoint-rollback")
        {
            await transaction.RollbackAsync("edit");
            (await FenceOpenAsync(publisher, candidate)).Should().BeFalse(
                "a rolled-back subtransaction must not release its still-open top-level fence");
        }

        if (outcome == "rollback")
        {
            await transaction.RollbackAsync();
        }
        else
        {
            await transaction.CommitAsync();
        }

        (await FenceOpenAsync(publisher, candidate)).Should().BeTrue();
        (await PublishedAsync(publisher, candidate)).Should().Be(higherGeneration);
        var retained = outcome is "commit" or "savepoint-commit";
        (await ScalarAsync(publisher, $"SELECT EXISTS (SELECT 1 FROM changes WHERE generation = {lowerGeneration})"))
            .Should().Be(retained);
    }

    [IntegrationTest(Timeout = 180000)]
    public async Task TransactionFence_LaterWriter_RemainsOpenWithoutEnteringCapturedPrefix()
    {
        await using var scope = await FenceScope.CreateAsync(fixture);
        await using var writer = await scope.OpenAsync();
        await using var publisher = await scope.OpenAsync();
        var committed = await CommitHigherAsync(writer);
        var candidate = await CaptureAsync(publisher);

        await using var transaction = await writer.BeginTransactionAsync();
        await ScalarAsync(writer, "SELECT pg_current_xact_id()::text");
        var later = await AllocateAsync(writer);
        later.Should().BeGreaterThan(candidate.AllocatedThrough);
        await ExecuteAsync(writer, $"INSERT INTO changes VALUES ({later})");

        (await FenceOpenAsync(publisher, candidate)).Should().BeTrue();
        (await PublishedAsync(publisher, candidate)).Should().Be(committed);
        await transaction.CommitAsync();
        (await PublishedAsync(publisher, candidate)).Should().Be(committed,
            "publication must stay bounded even after a later allocation commits");
    }

    [IntegrationTest(Timeout = 180000)]
    public async Task TransactionFence_AllocationBeforeXid_CanPublishPastAnUnfinishedLowerWriter()
    {
        await using var scope = await FenceScope.CreateAsync(fixture);
        await using var lower = await scope.OpenAsync();
        await using var higher = await scope.OpenAsync();
        await using var publisher = await scope.OpenAsync();
        await using var transaction = await AllocateWithoutXidAsync(lower, higher);
        var lowerGeneration = (long)(await ScalarAsync(lower, "SELECT last_value FROM generations"))!;
        var higherGeneration = await CommitHigherAsync(higher);
        var candidate = await CaptureAsync(publisher);

        (await FenceOpenAsync(publisher, candidate)).Should().BeTrue();
        var published = await PublishedAsync(publisher, candidate);
        published.Should().Be(higherGeneration);
        lowerGeneration.Should().BeLessThan(published);
        await ExecuteAsync(lower, $"INSERT INTO changes VALUES ({lowerGeneration})");
        await transaction.CommitAsync();

        (await ScalarAsync(publisher, $"SELECT EXISTS (SELECT 1 FROM changes WHERE generation = {lowerGeneration})"))
            .Should().Be(true, "an allocator without an assigned XID evades the captured snapshot fence");
    }

    [IntegrationTest(Timeout = 180000)]
    public async Task SnapshotFence_SnapshotBeforeSequence_CanPublishPastAnUnfinishedLowerWriter()
    {
        await using var scope = await FenceScope.CreateAsync(fixture);
        await using var lower = await scope.OpenAsync();
        await using var higher = await scope.OpenAsync();
        await using var publisher = await scope.OpenAsync();
        var staleXmax = (string)(await ScalarAsync(publisher,
            "SELECT pg_snapshot_xmax(pg_current_snapshot())::text"))!;

        await using var transaction = await lower.BeginTransactionAsync();
        await ScalarAsync(lower, "SELECT pg_current_xact_id()::text");
        var lowerGeneration = await AllocateAsync(lower);
        await ExecuteAsync(lower, $"INSERT INTO changes VALUES ({lowerGeneration})");
        var higherGeneration = await CommitHigherAsync(higher);
        var allocated = (long)(await ScalarAsync(publisher, "SELECT last_value FROM generations"))!;
        var candidate = new Candidate(allocated, staleXmax);

        (await FenceOpenAsync(publisher, candidate)).Should().BeTrue(
            "a writer starting after the stale snapshot does not hold its xmin fence closed");
        (await PublishedAsync(publisher, candidate)).Should().Be(higherGeneration);
        lowerGeneration.Should().BeLessThan(higherGeneration);
        await transaction.CommitAsync();

        (await ScalarAsync(publisher, $"SELECT EXISTS (SELECT 1 FROM changes WHERE generation = {lowerGeneration})"))
            .Should().Be(true, "reading the sequence after the snapshot admitted an unfinished lower allocation");
    }

    // The cut is an explicitly assigned XID after the sequence read. Snapshot xmax
    // alone follows completed XIDs and can omit a newer unfinished allocator.
    private readonly record struct Candidate(long AllocatedThrough, string TransactionCut);

    private static async Task<Candidate> CaptureAsync(NpgsqlConnection publisher)
    {
        var allocated = (long)(await ScalarAsync(publisher, "SELECT last_value FROM generations"))!;
        var cut = (string)(await ScalarAsync(publisher, "SELECT pg_current_xact_id()::text"))!;
        return new Candidate(allocated, cut);
    }

    private static async Task<bool> FenceOpenAsync(NpgsqlConnection publisher, Candidate candidate) =>
        (bool)(await ScalarAsync(publisher,
            $"SELECT pg_snapshot_xmin(pg_current_snapshot()) >= '{candidate.TransactionCut}'::xid8"))!;

    private static async Task<long> PublishedAsync(NpgsqlConnection publisher, Candidate candidate) =>
        (long)(await ScalarAsync(publisher,
            $"SELECT COALESCE(MAX(generation), 0) FROM changes WHERE generation <= {candidate.AllocatedThrough}"))!;

    private static async Task<long> CommitHigherAsync(NpgsqlConnection writer)
    {
        await using var transaction = await writer.BeginTransactionAsync();
        await ScalarAsync(writer, "SELECT pg_current_xact_id()::text");
        var generation = await AllocateAsync(writer);
        await ExecuteAsync(writer, $"INSERT INTO changes VALUES ({generation})");
        await transaction.CommitAsync();
        return generation;
    }

    private static async Task<NpgsqlTransaction> AllocateWithoutXidAsync(
        NpgsqlConnection writer, NpgsqlConnection primer)
    {
        // A checkpoint can make the next nextval emit WAL and assign an XID.
        // Discard that setup attempt; only an unassigned allocation proves the case.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await AllocateAsync(primer);
            var transaction = await writer.BeginTransactionAsync();
            try
            {
                await AllocateAsync(writer);
                if (await ScalarAsync(writer, "SELECT pg_current_xact_id_if_assigned()::text") is DBNull)
                {
                    return transaction;
                }

                await transaction.RollbackAsync();
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }

            await transaction.DisposeAsync();
        }

        throw new InvalidOperationException("Could not establish an allocation without an assigned XID.");
    }

    private static async Task<long> AllocateAsync(NpgsqlConnection writer) =>
        (long)(await ScalarAsync(writer, "SELECT nextval('generations')"))!;

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FenceScope(PostgresFixture database, string schema) : IAsyncDisposable
    {
        public static async Task<FenceScope> CreateAsync(PostgresFixture database)
        {
            var schema = await database.CreateIsolatedSchemaAsync("generation_fence");
            var scope = new FenceScope(database, schema);
            try
            {
                await using var connection = await scope.OpenAsync();
                await ExecuteAsync(connection, """
                    CREATE SEQUENCE generations CACHE 1 NO CYCLE;
                    CREATE TABLE changes (generation bigint PRIMARY KEY);
                    """);
                return scope;
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async Task<NpgsqlConnection> OpenAsync()
        {
            var connection = await database.DataSource.OpenConnectionAsync();
            try
            {
                await ExecuteAsync(connection, $"SET search_path TO \"{schema}\"");
                return connection;
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync() => await database.DropSchemaAsync(schema);
    }
}

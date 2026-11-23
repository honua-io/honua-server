// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Infrastructure.Abstractions;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

[Collection("FeatureChangePrefix")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class FeatureChangeWriterConcurrencyTests(FeatureChangeDatabaseFixture database)
    : IClassFixture<FeatureChangeDatabaseFixture>, IAsyncLifetime
{
    private const int FirstLayer = 734501;
    private const int SecondLayer = 734502;
    private const long FirstObject = 900001;
    private const long EditedObject = 900002;
    private long _cursor;

    public async Task InitializeAsync()
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, FirstLayer);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await ExecuteAsync(connection, """
            TRUNCATE honua.features, honua.feature_changes;
            ALTER SEQUENCE honua.sync_generation RESTART WITH 1;
            UPDATE honua.feature_change_prefix SET published = 0, pending = NULL, fence = NULL, pending_writers = NULL;
            INSERT INTO honua.features (layer_id, objectid, attributes)
            VALUES (734502, 900002, '{"name":"before edit"}'::jsonb);
            """);
        _cursor = await SettleAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [IntegrationTest(Timeout = 120000)]
    public async Task Write_DifferentLayers_SecondCommitsBeforeFirstWithoutLosingResume()
    {
        await using var first = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await first.BeginTransactionAsync();
        await InsertAsync(first, FirstLayer);
        var firstGeneration = await GenerationAsync(first);

        await WriteSecondAsync(SecondLayer);
        var secondGeneration = await HeadAsync();
        secondGeneration.Should().BeGreaterThan(firstGeneration);
        await AssertHeldAsync();
        await transaction.CommitAsync();

        await AssertResumedAsync([firstGeneration, secondGeneration]);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Write_SameLayerDistinctRows_SecondCommitsBeforeFirstWithoutLosingResume()
    {
        await using var first = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await first.BeginTransactionAsync();
        await InsertAsync(first, SecondLayer);
        var firstGeneration = await GenerationAsync(first);

        await WriteSecondAsync(SecondLayer);
        var secondGeneration = await HeadAsync();
        secondGeneration.Should().BeGreaterThan(firstGeneration);
        await AssertHeldAsync();
        await transaction.CommitAsync();

        await AssertResumedAsync([firstGeneration, secondGeneration]);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Write_FirstRollsBack_ResumeReturnsOnlyCommittedSecondWriter()
    {
        await using var first = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await first.BeginTransactionAsync();
        await InsertAsync(first, FirstLayer);
        var abandonedGeneration = await GenerationAsync(first);

        await WriteSecondAsync(SecondLayer);
        var committedGeneration = await HeadAsync();
        committedGeneration.Should().BeGreaterThan(abandonedGeneration);
        await AssertHeldAsync();
        await transaction.RollbackAsync();

        await AssertResumedAsync([committedGeneration]);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Write_CopyAlongsideEdit_EditCommitsBeforeImportWithoutLosingRows()
    {
        await using var import = await database.DataSource.OpenConnectionAsync();
        await using var transaction = await import.BeginTransactionAsync();
        await using (var copy = await import.BeginTextImportAsync(
            "COPY honua.features (layer_id, objectid, attributes) FROM STDIN"))
        {
            await copy.WriteLineAsync("734501\t900003\t{\"name\":\"import one\"}");
            await copy.WriteLineAsync("734501\t900004\t{\"name\":\"import two\"}");
        }

        var importedHead = await GenerationAsync(import);
        await WriteSecondAsync(SecondLayer);
        var editedGeneration = await HeadAsync();
        editedGeneration.Should().BeGreaterThan(importedHead);
        await AssertHeldAsync();
        await transaction.CommitAsync();

        await AssertResumedAsync([importedHead - 1, importedHead, editedGeneration]);
    }

    [IntegrationTest(Timeout = 120000)]
    public async Task Write_BranchOverlayAlongsideDefault_DefaultDoesNotWaitForOverlay()
    {
        var version = Guid.NewGuid();
        await using var first = await database.DataSource.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand("""
            INSERT INTO honua.gdb_versions (version_id, version_name, owner)
            VALUES (@version, @name, 'sde')
            """, first))
        {
            create.Parameters.AddWithValue("version", version);
            create.Parameters.AddWithValue("name", version.ToString("N"));
            await create.ExecuteNonQueryAsync();
        }

        await using var transaction = await first.BeginTransactionAsync();
        await using (var overlay = new NpgsqlCommand("""
            INSERT INTO honua.version_edits (version_id, layer_id, objectid, operation, attributes)
            VALUES (@version, 734501, 900001, 1, '{"name":"overlay"}'::jsonb)
            """, first))
        {
            overlay.Parameters.AddWithValue("version", version);
            await overlay.ExecuteNonQueryAsync();
        }

        var overlayGeneration = await GenerationAsync(first);
        await WriteSecondAsync(SecondLayer);
        var defaultGeneration = await HeadAsync();
        defaultGeneration.Should().BeGreaterThan(overlayGeneration);
        await AssertHeldAsync();
        await transaction.CommitAsync();

        (await SettleAsync()).Should().Be(defaultGeneration);
        (await ReadThroughHorizonAsync()).Should().Equal(overlayGeneration, defaultGeneration);
    }

    // Every generation, branch rows included, after the cursor and within the horizon.
    private async Task<IReadOnlyList<long>> ReadThroughHorizonAsync()
    {
        await using var changes = database.DataSource.CreateCommand("""
            SELECT generation FROM honua.feature_changes
            WHERE generation > @cursor AND generation <= honua.feature_change_read_horizon()
            ORDER BY generation
            """);
        changes.Parameters.AddWithValue("cursor", _cursor);
        await using var reader = await changes.ExecuteReaderAsync();
        var generations = new List<long>();
        while (await reader.ReadAsync())
        {
            generations.Add(reader.GetInt64(0));
        }

        return generations;
    }

    private static async Task InsertAsync(NpgsqlConnection connection, int layer)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO honua.features (layer_id, objectid, attributes)
            VALUES (@layer, @object, '{"name":"first writer"}'::jsonb)
            """, connection);
        command.Parameters.AddWithValue("layer", layer);
        command.Parameters.AddWithValue("object", FirstObject);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WriteSecondAsync(int layer)
    {
        await using var second = await database.DataSource.OpenConnectionAsync();
        await ExecuteAsync(second, "SET statement_timeout = '3s'");
        await using var update = new NpgsqlCommand("""
            UPDATE honua.features SET attributes = '{"name":"second writer"}'::jsonb
            WHERE layer_id = @layer AND objectid = @object
            """, second);
        update.Parameters.AddWithValue("layer", layer);
        update.Parameters.AddWithValue("object", EditedObject);
        (await update.ExecuteNonQueryAsync()).Should().Be(1,
            "an unrelated row must commit while the first feature transaction remains open");
    }

    private async Task AssertHeldAsync()
    {
        (await TickAsync()).Should().Be(_cursor);
        (await TickAsync()).Should().Be(_cursor);
        var log = CreateLog();
        (await log.ReadHeadAsync(CancellationToken.None)).Should().Be(_cursor);
        (await log.ReadAsync(Query(), CancellationToken.None)).Should().BeEmpty();
    }

    private async Task AssertResumedAsync(long[] expected)
    {
        (await SettleAsync()).Should().Be(expected[^1]);
        var log = CreateLog();
        (await log.ReadHeadAsync(CancellationToken.None)).Should().Be(expected[^1]);
        var resumed = await log.ReadAsync(Query(), CancellationToken.None);
        resumed.Select(change => change.Cursor).Should().Equal(expected);
        (await log.ReadAsync(new FeatureChangeLogQuery(expected[^1], null, null,
            [FirstLayer, SecondLayer], 10), CancellationToken.None)).Should().BeEmpty();
    }

    private FeatureChangeLogQuery Query() => new(_cursor, null, null, [FirstLayer, SecondLayer], 10);

    private PostgresFeatureChangeLog CreateLog()
        => new(new JobStoreDatabase(database.DataSource),
            new RealtimeCapabilityLimits(Options.Create(new RealtimeOptions())), TimeProvider.System);

    private async Task<long> SettleAsync()
    {
        var horizon = 0L;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            horizon = await TickAsync();
        }

        return horizon;
    }

    private async Task<long> TickAsync()
    {
        await using var command = database.DataSource.CreateCommand("SELECT honua.publish_feature_change_prefix()");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> HeadAsync()
    {
        await using var command = database.DataSource.CreateCommand("SELECT MAX(generation) FROM honua.feature_changes");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> GenerationAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT currval('honua.sync_generation')", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

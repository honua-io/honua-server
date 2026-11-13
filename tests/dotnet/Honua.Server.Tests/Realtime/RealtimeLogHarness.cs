// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// The realtime log tables in a schema of their own. Each store created here is a separate task over
// the same tables.
internal sealed class RealtimeLogHarness : IAsyncDisposable
{
    private const string MigrationResource = "Honua.Server.Migrations.158_CreateRealtimeEventLog.sql";

    private readonly PostgresJobSubstrateHarness _jobs;
    private readonly DatabaseFixtureAdapter _fixture;

    private RealtimeLogHarness(PostgresJobSubstrateHarness jobs, DatabaseFixtureAdapter fixture)
    {
        _jobs = jobs;
        _fixture = fixture;
        Log = new PostgresRealtimeEventLog(jobs.Database);
    }

    public PostgresRealtimeEventLog Log { get; }

    public NpgsqlDataSource DataSource => _jobs.DataSource;

    public string ConnectionString => _fixture.ConnectionString;

    // applyMigration false leaves the schema without the realtime tables, as before migrations run.
    public static async Task<RealtimeLogHarness> CreateAsync(DatabaseFixtureAdapter fixture, bool applyMigration = true)
    {
        var jobs = await PostgresJobSubstrateHarness.CreateAsync(fixture, nameof(RealtimeLogHarness));
        var harness = new RealtimeLogHarness(jobs, fixture);
        if (applyMigration)
        {
            await harness.ApplyMigrationAsync();
        }

        return harness;
    }

    public async Task ApplyMigrationAsync()
    {
        await using var stream = typeof(PostgresRealtimeEventLog).Assembly.GetManifestResourceStream(MigrationResource)
            ?? throw new InvalidOperationException($"Embedded migration '{MigrationResource}' was not found.");
        using var reader = new StreamReader(stream);
        var migration = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{_jobs.Schema}\"", StringComparison.Ordinal);
        await _fixture.ExecuteDdlUnderLockAsync(migration);
    }

    public static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    public string Table(string name) => _jobs.Database.Table(name);

    public Task ExecuteAsync(string sql) => _jobs.ExecuteAsync(sql);

    public Task<T?> ScalarAsync<T>(string sql) => _jobs.ScalarAsync<T>(sql);

    public async Task<IReadOnlyList<long>> ReadSequencesAsync(string family)
    {
        await using var command = DataSource.CreateCommand(
            $"SELECT seq FROM {Table("realtime_events")} WHERE family = @family ORDER BY seq");
        command.Parameters.AddWithValue("family", family);
        await using var rows = await command.ExecuteReaderAsync();
        var sequences = new List<long>();
        while (await rows.ReadAsync())
        {
            sequences.Add(rows.GetInt64(0));
        }

        return sequences;
    }

    public ValueTask DisposeAsync() => _jobs.DisposeAsync();
}

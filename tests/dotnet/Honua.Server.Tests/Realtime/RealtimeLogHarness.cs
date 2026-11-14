// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Features.Realtime;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// The realtime log tables in a schema of their own. Each store, dispatcher or provider created here
// is a separate task over the same tables.
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

    public static async Task<RealtimeLogHarness> CreateAsync(DatabaseFixtureAdapter fixture)
    {
        var harness = await CreateWithoutMigrationAsync(fixture);
        await harness.ApplyMigrationAsync();
        return harness;
    }

    public static async Task<RealtimeLogHarness> CreateWithoutMigrationAsync(DatabaseFixtureAdapter fixture)
    {
        var jobs = await PostgresJobSubstrateHarness.CreateAsync(fixture, nameof(RealtimeLogHarness));
        return new RealtimeLogHarness(jobs, fixture);
    }

    public Task ApplyMigrationAsync() => ApplyMigrationAsync(MigrationResource);

    public async Task ApplyMigrationAsync(string resourceName)
    {
        await using var stream = typeof(PostgresRealtimeEventLog).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        var migration = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{_jobs.Schema}\"", StringComparison.Ordinal);
        await _fixture.ExecuteDdlUnderLockAsync(migration);
    }

    public static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value);

    // A task's dispatcher over this schema.
    public RealtimeDispatcher CreateDispatcher(RealtimeOptions? options = null)
        => new(Log, Options.Create(options ?? new RealtimeOptions()), NullLogger<RealtimeDispatcher>.Instance);

    // A task's listener on this database.
    public RealtimeListener CreateListener(RealtimeOptions options, string? connectionString = null)
        => RealtimeListener.Create(
            connectionString ?? ConnectionString,
            Options.Create(options),
            NullLogger<RealtimeListener>.Instance);

    // A serving task: its dispatcher following the log, woken by its listener when it has one.
    public RealtimeTailBackgroundService CreateTail(RealtimeDispatcher dispatcher, RealtimeListener? listener, RealtimeOptions options)
        => new(dispatcher, new RealtimePresenceBatcher(Log), listener, Options.Create(options), NullLogger<RealtimeTailBackgroundService>.Instance);

    public ILeaseStore CreateLeaseStore() => new PostgresLeaseStore(_jobs.CreateNodeJobStore());

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

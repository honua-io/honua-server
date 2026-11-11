// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.StaleAttemptWorker;

// Signals and counters two test processes share through one PostgreSQL table: a value per key,
// or a value per field of a key.
public sealed class TestSignals(NpgsqlDataSource dataSource, string schema)
{
    private const string ValueField = "";

    private readonly string _table = $"\"{schema}\".test_signals";

    public async Task CreateTableAsync()
    {
        await using var command = dataSource.CreateCommand(
            $"CREATE TABLE IF NOT EXISTS {_table} (key TEXT NOT NULL, field TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY (key, field))");
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public Task SetAsync(string key, string value) => SetFieldAsync(key, ValueField, value);

    public Task<string?> GetAsync(string key) => GetFieldAsync(key, ValueField);

    public async Task<bool> ExistsAsync(string key)
    {
        await using var command = dataSource.CreateCommand($"SELECT 1 FROM {_table} WHERE key = @key LIMIT 1");
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) is not null;
    }

    public async Task DeleteAsync(IReadOnlyCollection<string> keys)
    {
        await using var command = dataSource.CreateCommand($"DELETE FROM {_table} WHERE key = ANY (@keys)");
        command.Parameters.AddWithValue("keys", keys.ToArray());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task SetFieldAsync(string key, string field, string value)
    {
        await using var command = dataSource.CreateCommand(
            $"INSERT INTO {_table} (key, field, value) VALUES (@key, @field, @value) "
            + "ON CONFLICT (key, field) DO UPDATE SET value = EXCLUDED.value");
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("field", field);
        command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task IncrementFieldAsync(string key, string field)
    {
        await using var command = dataSource.CreateCommand(
            $"INSERT INTO {_table} (key, field, value) VALUES (@key, @field, '1') "
            + $"ON CONFLICT (key, field) DO UPDATE SET value = ({_table}.value::bigint + 1)::text");
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("field", field);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<string?> GetFieldAsync(string key, string field)
    {
        await using var command = dataSource.CreateCommand($"SELECT value FROM {_table} WHERE key = @key AND field = @field");
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("field", field);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetFieldsAsync(string key)
    {
        await using var command = dataSource.CreateCommand($"SELECT field, value FROM {_table} WHERE key = @key");
        command.Parameters.AddWithValue("key", key);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            fields[reader.GetString(0)] = reader.GetString(1);
        }

        return fields;
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Server.Tests.Infrastructure;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// One layer of honua.features with the change-tracking trigger, removed with its changes on dispose.
internal sealed class FeatureChangeTestLayer(DatabaseFixtureAdapter database, int layerId) : IAsyncDisposable
{
    public int LayerId => layerId;

    public string Scope => layerId.ToString(CultureInfo.InvariantCulture);

    public static async Task<FeatureChangeTestLayer> CreateAsync(DatabaseFixtureAdapter database, int layerId)
    {
        var layer = new FeatureChangeTestLayer(database, layerId);
        await layer.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS honua.features (
                objectid BIGSERIAL PRIMARY KEY,
                layer_id INT NOT NULL,
                geometry GEOMETRY,
                attributes JSONB,
                created_at TIMESTAMPTZ DEFAULT NOW(),
                updated_at TIMESTAMPTZ DEFAULT NOW());
            DROP TRIGGER IF EXISTS trigger_track_feature_changes ON honua.features;
            CREATE TRIGGER trigger_track_feature_changes
                AFTER INSERT OR UPDATE OR DELETE ON honua.features
                FOR EACH ROW EXECUTE FUNCTION honua.track_feature_changes();
            """);
        await layer.CleanupAsync();
        return layer;
    }

    public Task<long> HeadAsync()
        => ScalarAsync<long>("SELECT COALESCE(max(generation), 0) FROM honua.feature_changes");

    public Task<long> InsertAsync(string attributes)
        => ScalarAsync<long>(
            """
            INSERT INTO honua.features (layer_id, geometry, attributes)
            VALUES (@layer, ST_SetSRID(ST_MakePoint(1, 2), 4326), @attributes::jsonb)
            RETURNING objectid
            """,
            ("attributes", attributes));

    public Task UpdateAsync(long objectId, string attributes)
        => ExecuteAsync(
            "UPDATE honua.features SET attributes = @attributes::jsonb WHERE objectid = @object AND layer_id = @layer",
            ("attributes", attributes),
            ("object", objectId));

    public Task DeleteAsync(long objectId)
        => ExecuteAsync("DELETE FROM honua.features WHERE objectid = @object AND layer_id = @layer", ("object", objectId));

    public Task InsertBranchChangeAsync()
        => ExecuteAsync("""
            INSERT INTO honua.feature_changes (generation, layer_id, objectid, operation, changed_at, version_id)
            VALUES (nextval('honua.sync_generation'), @layer, 1, 2, now(), gen_random_uuid())
            """);

    public async Task<IReadOnlyList<long>> GenerationsAsync()
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT generation FROM honua.feature_changes WHERE layer_id = @layer AND version_id IS NULL ORDER BY generation",
            connection);
        command.Parameters.AddWithValue("layer", layerId);
        await using var reader = await command.ExecuteReaderAsync();
        var generations = new List<long>();
        while (await reader.ReadAsync())
        {
            generations.Add(reader.GetInt64(0));
        }

        return generations;
    }

    public async ValueTask DisposeAsync() => await CleanupAsync();

    private Task CleanupAsync()
        => ExecuteAsync("""
            DELETE FROM honua.features WHERE layer_id = @layer;
            DELETE FROM honua.feature_changes WHERE layer_id = @layer;
            """);

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = Command(sql, connection, parameters);
        _ = await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = Command(sql, connection, parameters);
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), CultureInfo.InvariantCulture);
    }

    private NpgsqlCommand Command(string sql, NpgsqlConnection connection, (string Name, object Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection);
        if (sql.Contains("@layer", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("layer", layerId);
        }

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}

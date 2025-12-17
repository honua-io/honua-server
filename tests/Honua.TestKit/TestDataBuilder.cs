using Npgsql;
using NpgsqlTypes;

namespace Honua.TestKit;

/// <summary>
/// Fluent builder for creating test data in PostgreSQL/PostGIS.
/// </summary>
public sealed class TestDataBuilder
{
    private readonly PostgresFixture _fixture;
    private readonly List<Func<Task>> _actions = [];

    public TestDataBuilder(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Create a test table with geometry column.
    /// </summary>
    public TestDataBuilder WithTable(string tableName, string geometryType = "POINT", int srid = 4326)
    {
        _actions.Add(async () =>
        {
            await _fixture.ExecuteAsync($"""
                CREATE TABLE IF NOT EXISTS {tableName} (
                    id SERIAL PRIMARY KEY,
                    name TEXT,
                    description TEXT,
                    created_at TIMESTAMPTZ DEFAULT NOW(),
                    geom GEOMETRY({geometryType}, {srid})
                );
                CREATE INDEX IF NOT EXISTS idx_{tableName}_geom ON {tableName} USING GIST (geom);
                """);
        });
        return this;
    }

    /// <summary>
    /// Insert a point feature.
    /// </summary>
    public TestDataBuilder WithPoint(string tableName, string name, double lon, double lat)
    {
        _actions.Add(async () =>
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {tableName} (name, geom)
                VALUES (@name, ST_SetSRID(ST_MakePoint(@lon, @lat), 4326))
                """;
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("lon", lon);
            cmd.Parameters.AddWithValue("lat", lat);
            await cmd.ExecuteNonQueryAsync();
        });
        return this;
    }

    /// <summary>
    /// Insert a polygon feature from WKT.
    /// </summary>
    public TestDataBuilder WithPolygon(string tableName, string name, string wkt, int srid = 4326)
    {
        _actions.Add(async () =>
        {
            await using var conn = await _fixture.GetConnectionAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {tableName} (name, geom)
                VALUES (@name, ST_SetSRID(ST_GeomFromText(@wkt), @srid))
                """;
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("wkt", wkt);
            cmd.Parameters.AddWithValue("srid", srid);
            await cmd.ExecuteNonQueryAsync();
        });
        return this;
    }

    /// <summary>
    /// Insert multiple points in a grid pattern.
    /// </summary>
    public TestDataBuilder WithPointGrid(string tableName, string namePrefix, double startLon, double startLat, int rows, int cols, double spacing = 0.01)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                var name = $"{namePrefix}_{r}_{c}";
                var lon = startLon + (c * spacing);
                var lat = startLat + (r * spacing);
                WithPoint(tableName, name, lon, lat);
            }
        }
        return this;
    }

    /// <summary>
    /// Execute custom SQL.
    /// </summary>
    public TestDataBuilder WithSql(string sql)
    {
        _actions.Add(() => _fixture.ExecuteAsync(sql));
        return this;
    }

    /// <summary>
    /// Build and execute all actions.
    /// </summary>
    public async Task BuildAsync()
    {
        foreach (var action in _actions)
        {
            await action();
        }
    }
}

/// <summary>
/// Extension methods for test data setup.
/// </summary>
public static class TestDataExtensions
{
    public static TestDataBuilder CreateTestData(this PostgresFixture fixture)
    {
        return new TestDataBuilder(fixture);
    }
}

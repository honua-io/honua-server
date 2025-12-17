// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Bogus;

namespace Honua.TestKit;

/// <summary>
/// Fluent builder for creating test data in PostgreSQL/PostGIS.
/// Supports schema-based isolation for parallel tests.
/// </summary>
public sealed class TestDataBuilder
{
    private readonly PostgresFixture _fixture;
    private readonly List<Func<Task>> _actions = [];
    private readonly string? _schema;
    private readonly Faker _faker = new();

    public TestDataBuilder(PostgresFixture fixture, string? schema = null)
    {
        _fixture = fixture;
        _schema = schema;
    }

    /// <summary>
    /// Create a test table with geometry column.
    /// </summary>
    public TestDataBuilder WithTable(string tableName, string geometryType = "POINT", int srid = 4326, Dictionary<string, string>? additionalColumns = null)
    {
        _actions.Add(async () =>
        {
            var columnDefs = new List<string>
            {
                "id SERIAL PRIMARY KEY",
                "name TEXT",
                "description TEXT",
                "created_at TIMESTAMPTZ DEFAULT NOW()",
                $"geom GEOMETRY({geometryType}, {srid})"
            };

            if (additionalColumns is not null)
            {
                columnDefs.AddRange(additionalColumns.Select(kvp => $"{kvp.Key} {kvp.Value}"));
            }

            var sql = $"""
                CREATE TABLE IF NOT EXISTS {tableName} (
                    {string.Join(",\n    ", columnDefs)}
                );
                CREATE INDEX IF NOT EXISTS idx_{tableName}_geom ON {tableName} USING GIST (geom);
                """;

            await _fixture.ExecuteAsync(sql, _schema);
        });
        return this;
    }

    /// <summary>
    /// Insert a point feature.
    /// </summary>
    public TestDataBuilder WithPoint(string tableName, string name, double lon, double lat, Dictionary<string, object>? additionalValues = null)
    {
        _actions.Add(async () =>
        {
            await using var conn = await _fixture.GetConnectionAsync(_schema);
            await using var cmd = conn.CreateCommand();

            var columns = new List<string> { "name", "geom" };
            var values = new List<string> { "@name", "ST_SetSRID(ST_MakePoint(@lon, @lat), 4326)" };

            if (additionalValues is not null)
            {
                foreach (var (key, value) in additionalValues)
                {
                    columns.Add(key);
                    values.Add($"@{key}");
                    cmd.Parameters.AddWithValue(key, value);
                }
            }

            cmd.CommandText = $"""
                INSERT INTO {tableName} ({string.Join(", ", columns)})
                VALUES ({string.Join(", ", values)})
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
    public TestDataBuilder WithPolygon(string tableName, string name, string wkt, int srid = 4326, Dictionary<string, object>? additionalValues = null)
    {
        _actions.Add(async () =>
        {
            await using var conn = await _fixture.GetConnectionAsync(_schema);
            await using var cmd = conn.CreateCommand();

            var columns = new List<string> { "name", "geom" };
            var values = new List<string> { "@name", "ST_SetSRID(ST_GeomFromText(@wkt), @srid)" };

            if (additionalValues is not null)
            {
                foreach (var (key, value) in additionalValues)
                {
                    columns.Add(key);
                    values.Add($"@{key}");
                    cmd.Parameters.AddWithValue(key, value);
                }
            }

            cmd.CommandText = $"""
                INSERT INTO {tableName} ({string.Join(", ", columns)})
                VALUES ({string.Join(", ", values)})
                """;
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("wkt", wkt);
            cmd.Parameters.AddWithValue("srid", srid);
            await cmd.ExecuteNonQueryAsync();
        });
        return this;
    }

    /// <summary>
    /// Insert a linestring feature from coordinates.
    /// </summary>
    public TestDataBuilder WithLineString(string tableName, string name, IEnumerable<(double lon, double lat)> coordinates, int srid = 4326)
    {
        var points = string.Join(", ", coordinates.Select(c => $"{c.lon} {c.lat}"));
        var wkt = $"LINESTRING({points})";
        return WithPolygon(tableName, name, wkt, srid);
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
    /// Insert random points within a bounding box.
    /// </summary>
    public TestDataBuilder WithRandomPoints(string tableName, int count, double minLon, double minLat, double maxLon, double maxLat)
    {
        for (int i = 0; i < count; i++)
        {
            var name = _faker.Address.City();
            var lon = _faker.Random.Double(minLon, maxLon);
            var lat = _faker.Random.Double(minLat, maxLat);
            WithPoint(tableName, name, lon, lat);
        }
        return this;
    }

    /// <summary>
    /// Insert a circle polygon (approximated with points).
    /// </summary>
    public TestDataBuilder WithCircle(string tableName, string name, double centerLon, double centerLat, double radiusMeters, int srid = 4326)
    {
        _actions.Add(async () =>
        {
            await using var conn = await _fixture.GetConnectionAsync(_schema);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO {tableName} (name, geom)
                VALUES (@name, ST_Buffer(ST_SetSRID(ST_MakePoint(@lon, @lat), @srid)::geography, @radius)::geometry)
                """;
            cmd.Parameters.AddWithValue("name", name);
            cmd.Parameters.AddWithValue("lon", centerLon);
            cmd.Parameters.AddWithValue("lat", centerLat);
            cmd.Parameters.AddWithValue("radius", radiusMeters);
            cmd.Parameters.AddWithValue("srid", srid);
            await cmd.ExecuteNonQueryAsync();
        });
        return this;
    }

    /// <summary>
    /// Execute custom SQL.
    /// </summary>
    public TestDataBuilder WithSql(string sql)
    {
        _actions.Add(() => _fixture.ExecuteAsync(sql, _schema));
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
    /// <summary>
    /// Create a test data builder for the public schema.
    /// </summary>
    public static TestDataBuilder CreateTestData(this PostgresFixture fixture)
    {
        return new TestDataBuilder(fixture);
    }

    /// <summary>
    /// Create a test data builder for a specific schema.
    /// </summary>
    public static TestDataBuilder CreateTestData(this PostgresFixture fixture, string schema)
    {
        return new TestDataBuilder(fixture, schema);
    }
}

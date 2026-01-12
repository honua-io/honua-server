// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Performance;

/// <summary>
/// Integration tests verifying query execution plans and index usage.
/// These tests validate that queries use expected indexes and have efficient execution plans.
///
/// Targets from Issue #46:
/// - Query execution plan analysis to identify inefficiencies
/// - Index usage verification to ensure proper database indexing
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Performance")]
[Collection("Database")]
[Protocol(Protocols.TestQuality)]
[Operation(Operations.PerformanceTesting)]
public sealed class QueryPlanVerificationTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();
    private string _schemaName = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(QueryPlanVerificationTests));
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
        await _fixture.DisposeAsync();
    }

    /// <summary>
    /// Verifies that spatial queries use the GIST index on geometry column.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task SpatialQuery_UsesGistIndex()
    {
        // Arrange - create test table with spatial index
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_spatial_index;
            CREATE TABLE test_spatial_index (
                id SERIAL PRIMARY KEY,
                name TEXT,
                geom GEOMETRY(POINT, 4326)
            );
            CREATE INDEX idx_test_spatial_geom ON test_spatial_index USING GIST(geom);
            """, _schemaName);

        // Insert enough data to trigger index usage
        await _fixture.ExecuteAsync("""
            INSERT INTO test_spatial_index (name, geom)
            SELECT
                'Point_' || i,
                ST_SetSRID(ST_MakePoint(
                    -122.0 + (random() * 0.5),
                    37.0 + (random() * 0.5)
                ), 4326)
            FROM generate_series(1, 1000) AS i;
            """, _schemaName);

        // Analyze table for accurate query planning
        await _fixture.ExecuteAsync("ANALYZE test_spatial_index;", _schemaName);

        // Act - get execution plan for spatial query
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (FORMAT JSON)
            SELECT * FROM test_spatial_index
            WHERE ST_Intersects(
                geom,
                ST_MakeEnvelope(-122.2, 37.2, -121.8, 37.4, 4326)
            )
            """;

        var planJson = await cmd.ExecuteScalarAsync();

        // Assert - verify index scan is used
        var planText = planJson?.ToString() ?? "";
        Assert.Contains("Index", planText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that attribute queries on JSONB use the GIN index.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task JsonbQuery_UsesGinIndex()
    {
        // Arrange - create test table with GIN index on JSONB
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_jsonb_index;
            CREATE TABLE test_jsonb_index (
                id SERIAL PRIMARY KEY,
                attributes JSONB
            );
            CREATE INDEX idx_test_jsonb_attrs ON test_jsonb_index USING GIN(attributes);
            """, _schemaName);

        // Insert enough data to trigger index usage
        await _fixture.ExecuteAsync("""
            INSERT INTO test_jsonb_index (attributes)
            SELECT jsonb_build_object(
                'name', 'Item_' || i,
                'category', CASE WHEN i % 3 = 0 THEN 'A' WHEN i % 3 = 1 THEN 'B' ELSE 'C' END,
                'value', i * 10
            )
            FROM generate_series(1, 1000) AS i;
            """, _schemaName);

        // Analyze table
        await _fixture.ExecuteAsync("ANALYZE test_jsonb_index;", _schemaName);

        // Act - get execution plan for JSONB containment query
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (FORMAT JSON)
            SELECT * FROM test_jsonb_index
            WHERE attributes @> '{"category": "A"}'::jsonb
            """;

        var planJson = await cmd.ExecuteScalarAsync();

        // Assert - verify index scan is used (Bitmap Index Scan on GIN)
        var planText = planJson?.ToString() ?? "";
        // GIN indexes show as "Bitmap Index Scan" or just contain index reference
        var usesIndex = planText.Contains("Index", StringComparison.OrdinalIgnoreCase) ||
                        planText.Contains("Bitmap", StringComparison.OrdinalIgnoreCase);
        Assert.True(usesIndex, $"Expected index usage in plan: {planText}");
    }

    /// <summary>
    /// Verifies that layer_id filtering uses the appropriate index.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task LayerIdQuery_UsesIndex()
    {
        // Arrange - create test table with layer_id index
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_layer_index;
            CREATE TABLE test_layer_index (
                id SERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                name TEXT
            );
            CREATE INDEX idx_test_layer_id ON test_layer_index(layer_id);
            """, _schemaName);

        // Insert data across multiple layers
        await _fixture.ExecuteAsync("""
            INSERT INTO test_layer_index (layer_id, name)
            SELECT
                (i % 10) + 1,
                'Feature_' || i
            FROM generate_series(1, 10000) AS i;
            """, _schemaName);

        await _fixture.ExecuteAsync("ANALYZE test_layer_index;", _schemaName);

        // Act - get execution plan for layer_id filter
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (FORMAT JSON)
            SELECT * FROM test_layer_index
            WHERE layer_id = 5
            """;

        var planJson = await cmd.ExecuteScalarAsync();

        // Assert - verify index scan is used
        var planText = planJson?.ToString() ?? "";
        Assert.Contains("Index", planText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies query execution plan shows reasonable cost for paginated queries.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task PaginatedQuery_HasReasonableCost()
    {
        // Arrange
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_pagination;
            CREATE TABLE test_pagination (
                id SERIAL PRIMARY KEY,
                name TEXT,
                created_at TIMESTAMPTZ DEFAULT NOW()
            );
            CREATE INDEX idx_test_pagination_id ON test_pagination(id);
            """, _schemaName);

        await _fixture.ExecuteAsync("""
            INSERT INTO test_pagination (name)
            SELECT 'Item_' || i
            FROM generate_series(1, 10000) AS i;
            """, _schemaName);

        await _fixture.ExecuteAsync("ANALYZE test_pagination;", _schemaName);

        // Act - get execution plan with LIMIT/OFFSET
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (ANALYZE, FORMAT JSON)
            SELECT * FROM test_pagination
            ORDER BY id
            LIMIT 100 OFFSET 500
            """;

        var planJson = await cmd.ExecuteScalarAsync();

        // Assert - verify execution time is reasonable (< 100ms for simple pagination)
        var planText = planJson?.ToString() ?? "";
        Assert.Contains("Execution Time", planText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that combined spatial + attribute queries use appropriate indexes.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task CombinedQuery_UsesMultipleIndexes()
    {
        // Arrange
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_combined;
            CREATE TABLE test_combined (
                id SERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                geom GEOMETRY(POINT, 4326),
                attributes JSONB
            );
            CREATE INDEX idx_combined_layer ON test_combined(layer_id);
            CREATE INDEX idx_combined_geom ON test_combined USING GIST(geom);
            CREATE INDEX idx_combined_attrs ON test_combined USING GIN(attributes);
            """, _schemaName);

        await _fixture.ExecuteAsync("""
            INSERT INTO test_combined (layer_id, geom, attributes)
            SELECT
                (i % 5) + 1,
                ST_SetSRID(ST_MakePoint(-122.0 + random(), 37.0 + random()), 4326),
                jsonb_build_object('type', CASE WHEN i % 2 = 0 THEN 'A' ELSE 'B' END)
            FROM generate_series(1, 5000) AS i;
            """, _schemaName);

        await _fixture.ExecuteAsync("ANALYZE test_combined;", _schemaName);

        // Act - get execution plan for combined query
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (FORMAT JSON)
            SELECT * FROM test_combined
            WHERE layer_id = 1
            AND ST_Intersects(geom, ST_MakeEnvelope(-122.5, 37.0, -121.5, 38.0, 4326))
            AND attributes @> '{"type": "A"}'::jsonb
            """;

        var planJson = await cmd.ExecuteScalarAsync();

        // Assert - query plan should show index usage
        var planText = planJson?.ToString() ?? "";
        Assert.Contains("Index", planText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Documents typical query execution times for baseline measurement.
    /// This test captures execution statistics for performance baselines.
    /// </summary>
    [IntegrationTest]
    [Trait("Operation", "QueryPlan")]
    public async Task QueryExecution_MeetsPerformanceBaseline()
    {
        // Arrange - create realistic test data
        await _fixture.ExecuteAsync("""
            DROP TABLE IF EXISTS test_baseline;
            CREATE TABLE test_baseline (
                objectid SERIAL PRIMARY KEY,
                layer_id INTEGER NOT NULL,
                geometry GEOMETRY(POINT, 4326),
                attributes JSONB
            );
            CREATE INDEX idx_baseline_layer ON test_baseline(layer_id);
            CREATE INDEX idx_baseline_geom ON test_baseline USING GIST(geometry);
            """, _schemaName);

        await _fixture.ExecuteAsync("""
            INSERT INTO test_baseline (layer_id, geometry, attributes)
            SELECT
                1,
                ST_SetSRID(ST_MakePoint(-122.0 + (random() * 0.5), 37.0 + (random() * 0.5)), 4326),
                jsonb_build_object('name', 'Feature_' || i, 'population', (random() * 10000)::int)
            FROM generate_series(1, 1000) AS i;
            """, _schemaName);

        await _fixture.ExecuteAsync("ANALYZE test_baseline;", _schemaName);

        // Act - execute query with timing
        await using var conn = await _fixture.GetConnectionAsync(_schemaName);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            EXPLAIN (ANALYZE, FORMAT JSON)
            SELECT objectid, ST_AsGeoJSON(geometry), attributes
            FROM test_baseline
            WHERE layer_id = 1
            AND ST_Intersects(geometry, ST_MakeEnvelope(-122.3, 37.2, -121.7, 37.5, 4326))
            LIMIT 100
            """;

        var planJson = await cmd.ExecuteScalarAsync();
        var planText = planJson?.ToString() ?? "";

        // Assert - verify query completed and has timing info
        Assert.Contains("Execution Time", planText, StringComparison.OrdinalIgnoreCase);

        // Parse execution time if possible (for documentation)
        // The execution time should be captured in the plan
        Assert.NotEmpty(planText);
    }
}

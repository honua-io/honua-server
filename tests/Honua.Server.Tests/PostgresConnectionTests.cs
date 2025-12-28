// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests;

/// <summary>
/// Integration tests verifying PostgreSQL/PostGIS connectivity.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresConnectionTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CanConnectToPostgres()
    {
        // Act
        await using var conn = await _fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = await cmd.ExecuteScalarAsync();

        // Assert
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task PostGisExtensionIsAvailable()
    {
        // Act
        await using var conn = await _fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PostGIS_Version()";
        var result = await cmd.ExecuteScalarAsync();

        // Assert
        Assert.NotNull(result);
        var version = result.ToString();
        Assert.NotNull(version);
        Assert.True(version.StartsWith("3.6", StringComparison.Ordinal), $"Expected PostGIS 3.6.x version, got: {version}");
    }

    [Fact]
    public async Task CanCreateSpatialTable()
    {
        // Arrange
        await _fixture.CreateTestData()
            .WithTable("test_points", "POINT", 4326)
            .WithPoint("test_points", "Test Point", -122.4194, 37.7749)
            .BuildAsync();

        // Act
        await using var conn = await _fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, ST_AsText(geom) FROM test_points";
        await using var reader = await cmd.ExecuteReaderAsync();

        // Assert
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Test Point", reader.GetString(0));
        Assert.Contains("POINT", reader.GetString(1));
    }

    [Fact]
    public async Task CanPerformSpatialQuery()
    {
        // Arrange
        await _fixture.CreateTestData()
            .WithTable("cities", "POINT", 4326)
            .WithPoint("cities", "San Francisco", -122.4194, 37.7749)
            .WithPoint("cities", "Oakland", -122.2711, 37.8044)
            .WithPoint("cities", "New York", -74.0060, 40.7128)
            .BuildAsync();

        // Act - find cities within 50km of San Francisco
        await using var conn = await _fixture.GetConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name FROM cities
            WHERE ST_DWithin(
                geom::geography,
                ST_SetSRID(ST_MakePoint(-122.4194, 37.7749), 4326)::geography,
                50000
            )
            ORDER BY name
            """;
        await using var reader = await cmd.ExecuteReaderAsync();

        // Assert
        var cities = new List<string>();
        while (await reader.ReadAsync())
        {
            cities.Add(reader.GetString(0));
        }

        Assert.Contains("San Francisco", cities);
        Assert.Contains("Oakland", cities);
        Assert.DoesNotContain("New York", cities);
    }
}

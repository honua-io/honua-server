// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using DbUp;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Xunit;

namespace Honua.Server.Tests;

/// <summary>
/// Tests for database migration functionality using DbUp
/// </summary>
[Protocol("Infrastructure")]
public sealed class DatabaseMigrationTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private string _connectionString = null!;
    private string _schemaName = null!;

    public async Task InitializeAsync()
    {
        await _postgres.InitializeAsync();
        _schemaName = await _postgres.CreateIsolatedSchemaAsync(nameof(DatabaseMigrationTests));
        _connectionString = _postgres.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    [IntegrationTest]
    [Operation("Migration")]
    public async Task DbUpMigrations_OnFreshDatabase_CreatesSchemaAndTables()
    {
        // Arrange
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithTransaction()
            .Build();

        // Act
        var result = upgrader.PerformUpgrade();

        // Assert
        result.Successful.Should().BeTrue("migrations should complete successfully");
        result.Scripts.Should().HaveCountGreaterThan(0, "at least one migration script should exist");

        // Verify schema was created
        await using var connection = await _postgres.GetConnectionAsync(_schemaName);

        // Check honua schema exists
        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'honua')";
        var schemaExists = (bool)(await schemaCmd.ExecuteScalarAsync())!;
        schemaExists.Should().BeTrue("honua schema should be created");

        // Check PostGIS extension is enabled
        await using var postgisCmd = connection.CreateCommand();
        postgisCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'postgis')";
        var postgisExists = (bool)(await postgisCmd.ExecuteScalarAsync())!;
        postgisExists.Should().BeTrue("PostGIS extension should be enabled");

        // Verify tables exist
        await using var tablesCmd = connection.CreateCommand();
        tablesCmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'honua'
            AND table_name IN ('services', 'layers', 'layer_fields')
            """;
        var tablesExist = (int)(long)(await tablesCmd.ExecuteScalarAsync())!;
        tablesExist.Should().Be(3, "all three core tables should exist");

        // Verify foreign key constraints
        await using var constraintsCmd = connection.CreateCommand();
        constraintsCmd.CommandText = """
            SELECT COUNT(*) FROM information_schema.table_constraints
            WHERE constraint_schema = 'honua'
            AND constraint_type = 'FOREIGN KEY'
            """;
        var constraintsExist = (int)(long)(await constraintsCmd.ExecuteScalarAsync())!;
        constraintsExist.Should().BeGreaterThan(0, "foreign key constraints should exist");

        // Verify indexes
        await using var indexesCmd = connection.CreateCommand();
        indexesCmd.CommandText = """
            SELECT COUNT(*) FROM pg_indexes
            WHERE schemaname = 'honua'
            AND indexname IN ('idx_layers_service_id', 'idx_layer_fields_layer_id')
            """;
        var indexesExist = (int)(long)(await indexesCmd.ExecuteScalarAsync())!;
        indexesExist.Should().Be(2, "performance indexes should exist");
    }

    [IntegrationTest]
    [Operation("Migration")]
    public async Task DbUpMigrations_OnExistingDatabase_IsIdempotent()
    {
        // Arrange
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(_connectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithTransaction()
            .Build();

        // Act - Run migrations twice
        var firstResult = upgrader.PerformUpgrade();
        var secondResult = upgrader.PerformUpgrade();

        // Assert
        firstResult.Successful.Should().BeTrue("first migration should succeed");
        secondResult.Successful.Should().BeTrue("second migration should succeed");

        firstResult.Scripts.Should().HaveCountGreaterThan(0, "first run should apply scripts");
        secondResult.Scripts.Should().BeEmpty("second run should apply no scripts");
    }

    [IntegrationTest]
    [Operation("Migration")]
    public async Task DbUpMigrations_WithInvalidConnectionString_FailsGracefully()
    {
        // Arrange
        var invalidConnectionString = "Host=invalid;Database=invalid;Username=invalid;Password=invalid";
        var upgrader = DeployChanges.To
            .PostgresqlDatabase(invalidConnectionString)
            .WithScriptsEmbeddedInAssembly(Assembly.GetAssembly(typeof(Program))!)
            .WithTransaction()
            .Build();

        // Act
        var result = upgrader.PerformUpgrade();

        // Assert
        result.Successful.Should().BeFalse("migration should fail with invalid connection");
        result.Error.Should().NotBeNull("error details should be provided");
    }
}
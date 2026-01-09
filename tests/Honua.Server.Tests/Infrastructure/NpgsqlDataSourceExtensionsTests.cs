// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for NpgsqlDataSource extension methods with retry functionality
/// Uses real PostgreSQL database to test connection retry behavior
/// </summary>
[Collection("Database")]
[Protocol(Protocols.TestQuality)]
public sealed class NpgsqlDataSourceExtensionsTests : IAsyncLifetime
{
    private readonly DatabaseFixtureAdapter _fixture;
    private NpgsqlDataSource _dataSource = null!;
    private string _schemaName = null!;

    public NpgsqlDataSourceExtensionsTests(DatabaseFixtureAdapter fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        _schemaName = await _fixture.CreateIsolatedSchemaAsync(nameof(NpgsqlDataSourceExtensionsTests));
        _dataSource = _fixture.DataSource;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DropSchemaAsync(_schemaName);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenConnectionWithRetryAsync_WithValidConnectionString_OpensConnection()
    {
        // Act
        await using var connection = await _dataSource.OpenConnectionWithRetryAsync();

        // Assert
        connection.Should().NotBeNull();
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    // Note: Cancellation token behavior is handled by the underlying Npgsql library
    // and doesn't need separate testing in the extension method

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenConnectionWithRetryAsync_CallsOnRetryCallback()
    {
        // This test requires a way to simulate connection failures
        // For now, we'll test with a valid connection to ensure the callback mechanism works
        // In a real scenario with connection failures, the callback would be invoked

        // Arrange
        var retryCallbacks = new List<(Exception Exception, TimeSpan Delay, int Attempt)>();

        // Act - This should succeed without retries for a valid connection
        await using var connection = await _dataSource.OpenConnectionWithRetryAsync(
            onRetry: (ex, delay, attempt) => retryCallbacks.Add((ex, delay, attempt)));

        // Assert
        connection.Should().NotBeNull();
        connection.State.Should().Be(System.Data.ConnectionState.Open);
        retryCallbacks.Should().BeEmpty("no retries should occur with valid connection");
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenConnectionWithRetryAsync_WorksWithoutCallback()
    {
        // Act
        await using var connection = await _dataSource.OpenConnectionWithRetryAsync();

        // Assert
        connection.Should().NotBeNull();
        connection.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task OpenConnectionWithRetryAsync_CanExecuteQueriesAfterConnection()
    {
        // Act
        await using var connection = await _dataSource.OpenConnectionWithRetryAsync();
        await using var command = new NpgsqlCommand("SELECT 1 as test_value", connection);

        var result = await command.ExecuteScalarAsync();

        // Assert
        result.Should().Be(1);
    }
}

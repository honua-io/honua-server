// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Security;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

/// <summary>
/// Comprehensive tests for the secure connection registry.
/// </summary>
/// <remarks>
/// Tests cover:
/// - CRUD operations for secure connections
/// - Error conditions and edge cases
/// - Connection health updates
/// </remarks>
[Collection("Database")]
[SecurityTest]
public class SecureConnectionRegistryTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly PostgresSecureConnectionRegistry _registry;

    public SecureConnectionRegistryTests(WebAppFixture fixture)
    {
        _fixture = fixture;
        var primaryProvider = _fixture.GetService<IPrimaryDatabaseConnectionProvider>();
        _registry = new PostgresSecureConnectionRegistry(
            primaryProvider,
            NullLogger<PostgresSecureConnectionRegistry>.Instance);
    }

    [Fact]
    public async Task CreateConnectionAsync_ValidEncryptedConnection_CreatesSuccessfully()
    {
        // Arrange
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-encrypted-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        // Act
        var result = await _registry.CreateConnectionAsync(connection);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(connection.Name, result.Name);
        Assert.Equal(connection.Host, result.Host);
        Assert.Equal(connection.Port, result.Port);
        Assert.Equal(connection.DatabaseName, result.DatabaseName);
        Assert.Equal(connection.Username, result.Username);
    }

    [Fact]
    public async Task CreateConnectionAsync_ValidSecretRefConnection_CreatesSuccessfully()
    {
        // Arrange
        var connection = DataConnection.CreateWithSecretReference(
            name: $"test-secret-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            secretRef: "env:TEST_DB_CONNECTION",
            secretType: "environment",
            createdBy: "test-user");

        // Act
        var result = await _registry.CreateConnectionAsync(connection);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(connection.Name, result.Name);
        Assert.Equal(connection.SecretRef, result.SecretRef);
        Assert.Equal(connection.SecretType, result.SecretType);
    }

    [Fact]
    public async Task CreateConnectionAsync_VerifyCaSslMode_RoundTripsSuccessfully()
    {
        // Arrange
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-verifyca-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user",
            sslMode: SslMode.VerifyCA);

        // Act
        var created = await _registry.CreateConnectionAsync(connection);
        var retrieved = await _registry.GetConnectionAsync(created.ConnectionId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(SslMode.VerifyCA, retrieved!.SslMode);
    }

    [Fact]
    public async Task CreateConnectionAsync_CustomProvider_RoundTripsProviderName()
    {
        // Arrange
        var connection = DataConnection.CreateWithSecretReference(
            name: $"test-provider-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            secretRef: "env:TEST_DB_CONNECTION",
            secretType: "environment",
            createdBy: "test-user");
        connection.Provider = "PostgreSQL";

        // Act
        var created = await _registry.CreateConnectionAsync(connection);
        var retrieved = await _registry.GetConnectionAsync(created.ConnectionId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("postgresql", retrieved!.NormalizedProvider);
    }

    [Fact]
    public async Task CreateConnectionAsync_DuplicateName_ThrowsInvalidOperationException()
    {
        // Arrange
        var connectionName = $"test-duplicate-{Guid.NewGuid():N}";
        var connection1 = DataConnection.CreateWithEncryptedCredentials(
            name: connectionName,
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        // Create first connection
        await _registry.CreateConnectionAsync(connection1);

        // Create duplicate
        var connection2 = DataConnection.CreateWithEncryptedCredentials(
            name: connectionName,
            host: "localhost",
            port: 5432,
            databaseName: "testdb2",
            username: "testuser2",
            encryptedConnectionString: new byte[] { 6, 7, 8, 9, 10 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _registry.CreateConnectionAsync(connection2));
    }

    [Fact]
    public async Task GetConnectionAsync_ExistingConnection_ReturnsConnection()
    {
        // Arrange
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-get-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        // Act
        var retrieved = await _registry.GetConnectionAsync(created.ConnectionId);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(created.ConnectionId, retrieved.ConnectionId);
        Assert.Equal(created.Name, retrieved.Name);
        Assert.Equal(created.Host, retrieved.Host);
    }

    [Fact]
    public async Task GetConnectionAsync_NonExistentConnection_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _registry.GetConnectionAsync(nonExistentId);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetConnectionByNameAsync_ExistingConnection_ReturnsConnection()
    {
        // Arrange
        var connectionName = $"test-get-by-name-{Guid.NewGuid():N}";
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: connectionName,
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        await _registry.CreateConnectionAsync(connection);

        // Act
        var retrieved = await _registry.GetConnectionByNameAsync(connectionName);

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal(connectionName, retrieved.Name);
    }

    [Fact]
    public async Task GetActiveConnectionsAsync_MultipleConnections_ReturnsOnlyActive()
    {
        // Arrange
        var baseName = $"test-active-{Guid.NewGuid():N}";

        var activeConnection = DataConnection.CreateWithEncryptedCredentials(
            name: $"{baseName}-active",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var inactiveConnection = new DataConnection
        {
            Name = $"{baseName}-inactive",
            Host = "localhost",
            Port = 5432,
            DatabaseName = "testdb",
            Username = "testuser",
            ConnectionStringEncrypted = new byte[] { 6, 7, 8, 9, 10 },
            EncryptionKeyVersion = 1,
            CreatedBy = "test-user",
            IsActive = false
        };

        await _registry.CreateConnectionAsync(activeConnection);
        await _registry.CreateConnectionAsync(inactiveConnection);

        // Act
        var activeConnections = await _registry.GetActiveConnectionsAsync();

        // Assert
        var testConnections = activeConnections
            .Where(c => c.Name.StartsWith(baseName, StringComparison.Ordinal))
            .ToList();
        Assert.Single(testConnections);
        Assert.Equal($"{baseName}-active", testConnections[0].Name);
    }

    [Fact]
    public async Task DeleteConnectionAsync_ExistingConnection_ReturnsTrue()
    {
        // Arrange
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-delete-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        // Act
        var deleted = await _registry.DeleteConnectionAsync(created.ConnectionId);

        // Assert
        Assert.True(deleted);

        // Verify connection is actually deleted
        var retrieved = await _registry.GetConnectionAsync(created.ConnectionId);
        Assert.Null(retrieved);
    }

    [Fact]
    public async Task DeleteConnectionAsync_NonExistentConnection_ReturnsFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var deleted = await _registry.DeleteConnectionAsync(nonExistentId);

        // Assert
        Assert.False(deleted);
    }

    [Fact]
    public async Task UpdateHealthStatusAsync_ExistingConnection_UpdatesSuccessfully()
    {
        // Arrange
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: $"test-health-{Guid.NewGuid():N}",
            host: "localhost",
            port: 5432,
            databaseName: "testdb",
            username: "testuser",
            encryptedConnectionString: new byte[] { 1, 2, 3, 4, 5 },
            encryptionKeyVersion: 1,
            createdBy: "test-user");

        var created = await _registry.CreateConnectionAsync(connection);

        // Act
        var updated = await _registry.UpdateHealthStatusAsync(created.ConnectionId, ConnectionHealthStatus.Healthy);

        // Assert
        Assert.True(updated);

        // Verify status was updated
        var retrieved = await _registry.GetConnectionAsync(created.ConnectionId);
        Assert.NotNull(retrieved);
        Assert.Equal(ConnectionHealthStatus.Healthy, retrieved.HealthStatus);
        Assert.NotNull(retrieved.LastHealthCheck);
    }
}

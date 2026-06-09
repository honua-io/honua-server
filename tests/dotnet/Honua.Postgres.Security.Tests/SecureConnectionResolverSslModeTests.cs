// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Postgres.Security.Tests;

/// <summary>
/// Tests for <see cref="SecureConnectionResolver"/> verifying that SSL/TLS
/// enforcement rejects plaintext-fallback SSL modes and that secret-reference
/// resolution rejects host/port mismatches against the registered connection.
/// </summary>
public sealed class SecureConnectionResolverSslModeTests
{
    [SecurityTest]
    [Theory]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public async Task ResolveConnectionStringAsync_SslRequiredWithFallbackMode_ThrowsInvalidOperation(string sslMode)
    {
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: "production-analytics",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            encryptedConnectionString: [1, 2, 3],
            encryptionKeyVersion: 1,
            createdBy: "test",
            sslRequired: true,
            sslMode: SslMode.Require);

        var resolver = new SecureConnectionResolver(
            new StubRegistry(connection),
            new StubEncryptionService($"Host=db.example.com;Port=5432;Database=analytics;Username=app;Password=secret;SslMode={sslMode}"),
            new ThrowingSecretResolver(),
            NullLogger<SecureConnectionResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync(connection.Name));

        Assert.Equal("Failed to resolve connection string for 'production-analytics'.", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.Contains("allows plaintext fallback", exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [SecurityTest]
    [Theory]
    [InlineData("Host=replica.example.com;Port=5432;Database=analytics;Username=app;Password=secret;SslMode=Require", "resolved host does not match configured host")]
    [InlineData("Host=db.example.com;Port=6432;Database=analytics;Username=app;Password=secret;SslMode=Require", "resolved port does not match configured port")]
    public async Task ResolveConnectionStringAsync_SecretReferenceHostOrPortMismatch_ThrowsInvalidOperation(
        string resolvedConnectionString,
        string expectedMessage)
    {
        var connection = DataConnection.CreateWithSecretReference(
            name: "production-analytics",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            secretRef: "env:PROD_DB_CONNECTION",
            secretType: "EnvironmentVariable",
            createdBy: "test",
            sslRequired: true,
            sslMode: SslMode.Require);

        var resolver = new SecureConnectionResolver(
            new StubRegistry(connection),
            new StubEncryptionService("Host=db.example.com;Port=5432;Database=analytics;Username=app;Password=secret;SslMode=Require"),
            new StubSecretResolver(resolvedConnectionString),
            NullLogger<SecureConnectionResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync(connection.Name));

        Assert.Equal("Failed to resolve connection string for 'production-analytics'.", exception.Message);
        Assert.NotNull(exception.InnerException);
        Assert.Contains(expectedMessage, exception.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubRegistry(DataConnection connection) : ISecureConnectionRegistry
    {
        private readonly DataConnection _connection = connection;

        public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task RegisterConnectionAsync(DataConnection connection)
            => Task.FromException(new NotSupportedException());

        public Task<DataConnection?> GetConnectionAsync(string connectionId)
            => Task.FromResult(string.Equals(connectionId, _connection.ConnectionId.ToString(), StringComparison.Ordinal) ? _connection : null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
            => Task.FromResult(string.Equals(connectionId, _connection.ConnectionId.ToString(), StringComparison.Ordinal) ? _connection : null);

        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(connectionId == _connection.ConnectionId ? _connection : null);

        public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(connectionName, _connection.Name, StringComparison.Ordinal) ? _connection : null);

        public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
            => Task.FromResult<IEnumerable<DataConnection>>([_connection]);

        public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<DataConnection>>([_connection]);

        public Task<bool> RemoveConnectionAsync(string connectionId)
            => Task.FromResult(false);

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
            => Task.FromResult(new Dictionary<string, ConnectionHealthStatus>());

        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);
    }

    private sealed class StubEncryptionService(string decryptedConnectionString) : IConnectionEncryptionService
    {
        private readonly string _decryptedConnectionString = decryptedConnectionString;

        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
            => Task.FromException<byte[]>(new NotSupportedException());

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromResult(_decryptedConnectionString);

        public Task<int> GetCurrentKeyVersionAsync() => Task.FromResult(1);

        public Task<int> RotateKeyAsync()
            => Task.FromException<int>(new NotSupportedException());

        public Task<bool> ValidateEncryptionAsync() => Task.FromResult(true);
    }

    private sealed class ThrowingSecretResolver : IConnectionSecretResolver
    {
        public string ProviderName => "ThrowingProvider";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromException<string?>(new NotSupportedException());

        public bool CanResolve(string secretKey)
            => false;

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new NotSupportedException());
    }

    private sealed class StubSecretResolver(string resolvedConnectionString) : IConnectionSecretResolver
    {
        private readonly string _resolvedConnectionString = resolvedConnectionString;

        public string ProviderName => "EnvironmentVariable";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(_resolvedConnectionString);

        public bool CanResolve(string secretKey)
            => true;

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => Task.FromResult(_resolvedConnectionString);
    }
}

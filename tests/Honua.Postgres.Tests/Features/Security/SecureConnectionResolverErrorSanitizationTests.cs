// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public sealed class SecureConnectionResolverErrorSanitizationTests
{
    [SecurityTest]
    [Fact]
    public async Task ResolveConnectionStringAsync_ResolverDependencyThrows_DoesNotExposeSensitiveMessage()
    {
        const string sensitiveMessage = "client_secret=leaked-value";
        var connection = DataConnection.CreateWithSecretReference(
            name: "production-analytics",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            secretRef: "azure:keyvault:myvault:my-secret",
            secretType: "azure",
            createdBy: "test");

        var resolver = new SecureConnectionResolver(
            new StubRegistry(connection),
            new StubEncryptionService(),
            new ThrowingSecretResolver(new InvalidOperationException(sensitiveMessage)),
            NullLogger<SecureConnectionResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync(connection.Name));

        exception.Message.Should().Be("Failed to resolve connection string for 'production-analytics'.");
        exception.Message.Should().NotContain("leaked-value");
        exception.Message.Should().NotContain(sensitiveMessage);
    }

    private sealed class StubRegistry(DataConnection connection) : ISecureConnectionRegistry
    {
        private readonly DataConnection _connection = connection;

        public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task RegisterConnectionAsync(DataConnection connection)
            => Task.CompletedTask;

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

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(connection);

        public Task<bool> RemoveConnectionAsync(string connectionId)
            => Task.FromResult(false);

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
            => Task.FromResult(new Dictionary<string, ConnectionHealthStatus>());

        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StubEncryptionService : IConnectionEncryptionService
    {
        public Task<byte[]> EncryptConnectionStringAsync(string connectionString)
            => Task.FromException<byte[]>(new NotSupportedException());

        public Task<string> DecryptConnectionStringAsync(byte[] encryptedData, int keyVersion)
            => Task.FromException<string>(new NotSupportedException());

        public Task<int> GetCurrentKeyVersionAsync() => Task.FromResult(1);

        public Task<int> RotateKeyAsync()
            => Task.FromException<int>(new NotSupportedException());

        public Task<bool> ValidateEncryptionAsync() => Task.FromResult(true);
    }

    private sealed class ThrowingSecretResolver(Exception exceptionToThrow) : IConnectionSecretResolver
    {
        private readonly Exception _exceptionToThrow = exceptionToThrow;

        public string ProviderName => "azure";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromException<string?>(_exceptionToThrow);

        public bool CanResolve(string secretKey)
            => true;

        public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromException<string>(_exceptionToThrow);
    }
}

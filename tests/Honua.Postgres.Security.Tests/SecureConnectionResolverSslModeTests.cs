using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Postgres.Security.Tests;

public sealed class SecureConnectionResolverSslModeTests
{
    [Theory]
    [InlineData("Allow")]
    [InlineData("Prefer")]
    public async Task ResolveConnectionStringAsyncSslRequiredRejectsFallbackSslModes(string sslMode)
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

    [Theory]
    [InlineData("Host=replica.example.com;Port=5432;Database=analytics;Username=app;Password=secret;SslMode=Require", "resolved host does not match configured host")]
    [InlineData("Host=db.example.com;Port=6432;Database=analytics;Username=app;Password=secret;SslMode=Require", "resolved port does not match configured port")]
    public async Task ResolveConnectionStringAsyncSecretReferenceRejectsHostOrPortMismatch(
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
            => Task.FromException<DataConnection>(new NotSupportedException());

        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(connectionId == _connection.ConnectionId ? _connection : null);

        public Task<DataConnection?> GetConnectionByNameAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(name, _connection.Name, StringComparison.Ordinal) ? _connection : null);

        public Task<IReadOnlyList<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DataConnection>>([_connection]);

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => Task.FromException<DataConnection>(new NotSupportedException());

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> UpdateHealthStatusAsync(Guid connectionId, ConnectionHealthStatus healthStatus, CancellationToken cancellationToken = default)
            => Task.FromResult(false);
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
        public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromException<string>(new NotSupportedException());

        public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public string[] GetSupportedProviders() => [];
    }

    private sealed class StubSecretResolver(string resolvedConnectionString) : IConnectionSecretResolver
    {
        private readonly string _resolvedConnectionString = resolvedConnectionString;

        public Task<string> ResolveConnectionStringAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(_resolvedConnectionString);

        public Task<bool> CanResolveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public string[] GetSupportedProviders() => ["EnvironmentVariable"];
    }
}

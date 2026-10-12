// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Security;

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

    [SecurityTest]
    [Theory]
    [InlineData("env:HONUA_TEST_UNLISTED_CONNECTION")]
    [InlineData("Host=db.example.com;Port=5432;Database=analytics;Username=app;Password={env:HONUA_TEST_LISTED_CONNECTION}")]
    [InlineData("Host=db.example.com;Port=5432;Database=analytics;Username=app;Password=inline")]
    public async Task ResolveConnectionStringAsync_StoredReferenceOutsideThePolicy_DoesNotResolve(string storedReference)
    {
        var connection = DataConnection.CreateWithSecretReference(
            name: "warehouse",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            secretRef: storedReference,
            secretType: "environment",
            createdBy: "test");
        var inner = new RecordingConnectionSecretResolver();
        var resolver = new SecureConnectionResolver(
            new StubRegistry(connection),
            new StubEncryptionService(),
            new RequestSecretReferenceResolver(
                inner,
                new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["HONUA_TEST_LISTED_CONNECTION"] },
                NullLogger<RequestSecretReferenceResolver>.Instance),
            NullLogger<SecureConnectionResolver>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveConnectionStringAsync(connection.Name));

        exception.Message.Should().Be("Failed to resolve connection string for 'warehouse'.");
        inner.Requested.Should().BeEmpty();
    }

    [SecurityTest]
    [Fact]
    public async Task ResolveConnectionStringAsync_StoredReferenceWithinThePolicy_Resolves()
    {
        const string resolved = "Host=db.example.com;Port=5432;Database=analytics;Username=app;Password=secret;SslMode=Require";
        var connection = DataConnection.CreateWithSecretReference(
            name: "warehouse",
            host: "db.example.com",
            port: 5432,
            databaseName: "analytics",
            username: "app",
            secretRef: "env:HONUA_TEST_LISTED_CONNECTION",
            secretType: "environment",
            createdBy: "test");
        var inner = new RecordingConnectionSecretResolver { Value = resolved };
        var resolver = new SecureConnectionResolver(
            new StubRegistry(connection),
            new StubEncryptionService(),
            new RequestSecretReferenceResolver(
                inner,
                new RequestSecretReferenceOptions { AllowedEnvironmentVariables = ["HONUA_TEST_LISTED_CONNECTION"] },
                NullLogger<RequestSecretReferenceResolver>.Instance),
            NullLogger<SecureConnectionResolver>.Instance);

        (await resolver.ResolveConnectionStringAsync(connection.Name)).Should().Be(resolved);
        inner.Requested.Should().Equal("env:HONUA_TEST_LISTED_CONNECTION");
    }

    private sealed class RecordingConnectionSecretResolver : IConnectionSecretResolver
    {
        public List<string> Requested { get; } = [];

        public string? Value { get; init; }

        public string ProviderName => "composite";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
        {
            Requested.Add(secretKey);
            return Task.FromResult(Value);
        }

        public bool CanResolve(string secretKey) => true;

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Stored references must not use template resolution.");

        public string[] GetSupportedProviders() => ["env"];
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

    private sealed class ThrowingSecretResolver(Exception exceptionToThrow) : IRequestSecretReferenceResolver
    {
        private readonly Exception _exceptionToThrow = exceptionToThrow;

        public RequestSecretReferenceDecision Evaluate(string? reference)
            => RequestSecretReferenceDecision.Permitted();

        public Task<string> ResolveAsync(string reference, CancellationToken cancellationToken = default)
            => Task.FromException<string>(_exceptionToThrow);
    }
}

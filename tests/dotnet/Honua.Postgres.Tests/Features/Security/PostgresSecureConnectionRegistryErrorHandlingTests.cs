// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Security;

[Collection("Security")]
public sealed class PostgresSecureConnectionRegistryErrorHandlingTests
{
    [SecurityTest]
    [Fact]
    public async Task GetActiveConnectionsAsync_InterfaceWrapper_PropagatesUnderlyingException()
    {
        // Regression: previous implementation used ContinueWith + .Result inside the
        // interface adapter, which wraps faults in AggregateException and risks
        // sync-over-async deadlocks under thread-pool starvation. We expect the
        // underlying exception type to bubble up unchanged.
        var expected = new InvalidOperationException("primary database unavailable");
        var provider = new ThrowingPrimaryDatabaseConnectionProvider(expected);
        ISecureConnectionRegistry registry = new PostgresSecureConnectionRegistry(
            provider,
            NullLogger<PostgresSecureConnectionRegistry>.Instance);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.GetActiveConnectionsAsync(CancellationToken.None));

        actual.Should().BeSameAs(expected);
    }

    [SecurityTest]
    [Fact]
    public async Task GetActiveConnectionsAsync_InterfaceWrapper_RespectsCancellation()
    {
        // The ContinueWith pre-fix flavour swallowed cancellation when the source
        // task had already completed. The async/await variant should observe a
        // pre-cancelled token before it ever opens a connection.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var provider = new ThrowingPrimaryDatabaseConnectionProvider(
            new OperationCanceledException(cts.Token));
        ISecureConnectionRegistry registry = new PostgresSecureConnectionRegistry(
            provider,
            NullLogger<PostgresSecureConnectionRegistry>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.GetActiveConnectionsAsync(cts.Token));
    }

    [SecurityTest]
    [Fact]
    public async Task CreateConnectionAsync_NonPostgresDuplicateMessage_DoesNotMapToDuplicateNameError()
    {
        var expectedException = new InvalidOperationException(
            "transient upstream failure while validating duplicate key metadata");
        var provider = new ThrowingPrimaryDatabaseConnectionProvider(expectedException);
        var registry = new PostgresSecureConnectionRegistry(
            provider,
            NullLogger<PostgresSecureConnectionRegistry>.Instance);

        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: "test-connection",
            host: "localhost",
            port: 5432,
            databaseName: "db",
            username: "user",
            encryptedConnectionString: [1, 2, 3],
            encryptionKeyVersion: 1,
            createdBy: "test");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CreateConnectionAsync(connection));

        exception.Message.Should().Be(expectedException.Message);
        exception.Should().BeSameAs(expectedException);
    }

    private sealed class ThrowingPrimaryDatabaseConnectionProvider(Exception exceptionToThrow) : IPrimaryDatabaseConnectionProvider
    {
        private readonly Exception _exceptionToThrow = exceptionToThrow;

        public string GetConnectionString()
            => "Host=localhost;Database=test;";

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => Task.FromException<DbConnection>(_exceptionToThrow);

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => Task.FromException<(DbConnection Connection, DbTransaction Transaction)>(_exceptionToThrow);

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => Task.FromException<T>(_exceptionToThrow);

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => Task.FromException(_exceptionToThrow);
    }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Collaboration.Operations;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Collaboration.Operations;
using Npgsql;

namespace Honua.TestKit;

// Exercises the production shared operation store from server cross-task integration tests.
internal static class PostgresCollaborationTestStore
{
    public static ISavedMapOperationLogRepository Create(NpgsqlDataSource dataSource, string schema) =>
        new PostgresSavedMapOperationLogRepository(new TestConnectionProvider(dataSource, schema),
            new MvpSavedMapOperationConflictPolicy(), schemaName: schema);

    private sealed class TestConnectionProvider(
        NpgsqlDataSource dataSource,
        string schemaName) : IAdoNetDatabaseConnectionProvider
    {
        public string GetConnectionString() => dataSource.ConnectionString;

        public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SET search_path TO \"{schemaName}\", public;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }

        public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
        {
            var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
                return (connection, transaction);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default) => operation();

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default) => operation();
    }

}

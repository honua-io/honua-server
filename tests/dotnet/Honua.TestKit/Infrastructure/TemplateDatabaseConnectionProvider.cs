// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Infrastructure.Middleware;
using Npgsql;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Template-database-mode connection provider. Opens connections from the per-test database
/// resolved by <see cref="TemplateDatabaseDataSourceRouter"/> (the test's own cloned
/// database, or the bootstrap database for header-less/background work). Used in place of the
/// production connection provider so the hot DB-access path routes per-test-database without a
/// scoped <see cref="NpgsqlDataSource"/> registration (whose container-owned disposal would
/// dispose the router's cached data source mid-run).
/// </summary>
/// <remarks>
/// When the ambient request targets a cloned per-test database, no <c>SET search_path</c> is
/// applied: feature tables live in that database's <c>public</c> schema and the data source's
/// default search_path already includes <c>public</c>. When the ambient value is a classic
/// per-test schema (a UseSeed fixture sharing the host) the bootstrap data source is used and
/// the schema search_path IS applied so schema isolation keeps working.
/// </remarks>
internal sealed class TemplateDatabaseConnectionProvider : IAdoNetDatabaseConnectionProvider
{
    private readonly TemplateDatabaseDataSourceRouter _router;
    private readonly PostgresFixture _fixture;

    public TemplateDatabaseConnectionProvider(TemplateDatabaseDataSourceRouter router, PostgresFixture fixture)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    public string GetConnectionString() => _router.Resolve().ConnectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var dataSource = _router.Resolve();
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var ambient = SchemaContext.AmbientCurrentSchema;
        if (!string.IsNullOrWhiteSpace(ambient) && !_fixture.IsIsolatedDatabase(ambient))
        {
            // Classic schema-mode ambient (e.g. a UseSeed fixture on the shared host).
            ValidateSchemaName(ambient);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SET search_path TO {ambient}, public;";
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken)
                .ConfigureAwait(false);
            return (connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<T> ExecuteWithDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await operation().ConfigureAwait(false);
    }

    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await operation().ConfigureAwait(false);
    }

    private static void ValidateSchemaName(string schemaName)
    {
        foreach (var ch in schemaName)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_')
            {
                throw new ArgumentException($"Invalid schema name '{schemaName}'.", nameof(schemaName));
            }
        }
    }
}

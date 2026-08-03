// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Postgres.Features.Infrastructure;
using Honua.Postgres.Features.Infrastructure.Resilience;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Postgres.Features.Raster;

/// <summary>
/// Attempt-scoped connection provider that applies the dedicated role and tenant/session fence
/// before returning any connection to raster operation code.
/// </summary>
internal sealed class PostgisRasterDatabaseConnectionProvider : IAdoNetDatabaseConnectionProvider
{
    private const string InitializeSessionSql = """
        SELECT
            set_config('honua.tenant_id', @tenant_id, false),
            set_config('honua.operation_id', @operation_id, false),
            set_config('honua.attempt', @attempt, false),
            set_config('statement_timeout', @statement_timeout, false),
            set_config('lock_timeout', @lock_timeout, false),
            set_config('idle_in_transaction_session_timeout', @idle_timeout, false),
            set_config('search_path', @search_path, false);
        """;

    private readonly PostgisRasterDataSource _dataSource;
    private readonly PostgisRasterExecutionOptions _options;
    private readonly string _tenantId;
    private readonly string _operationId;
    private readonly int _attempt;
    private readonly string _searchPathSchema;

    public PostgisRasterDatabaseConnectionProvider(
        PostgisRasterDataSource dataSource,
        PostgisRasterExecutionOptions options,
        string tenantId,
        string operationId,
        int attempt,
        string searchPathSchema)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tenantId = tenantId;
        _operationId = operationId;
        _attempt = attempt;
        _searchPathSchema = searchPathSchema;
    }

    public string GetConnectionString() => _dataSource.ConnectionString;

    public async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await VerifyRoleAsync(connection, cancellationToken).ConfigureAwait(false);
            await InitializeSessionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection.BeginTransactionAsync(
                isolationLevel,
                cancellationToken).ConfigureAwait(false);
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
        ArgumentNullException.ThrowIfNull(operation);
        return await _dataSource.DataSourceForResilience.ExecuteWithDeadlockRetryAsync(
            operation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteWithDeadlockRetryAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _dataSource.DataSourceForResilience.ExecuteWithDeadlockRetryAsync(
            operation,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static string BuildSearchPath(string schemaName)
    {
        if (!SchemaSearchPath.IsValidIdentifier(schemaName))
        {
            throw PostgisRasterGovernanceException.TenantSchemaUnavailable();
        }

        return $"\"{schemaName}\", public";
    }

    private async Task VerifyRoleAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_user;";
        var currentRole = (string?)await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentRole, _options.RequiredRole, StringComparison.Ordinal))
        {
            throw PostgisRasterGovernanceException.RoleMismatch();
        }
    }

    private async Task InitializeSessionAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = InitializeSessionSql;
        command.Parameters.AddWithValue("tenant_id", NpgsqlDbType.Text, _tenantId);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Text, _operationId);
        command.Parameters.AddWithValue(
            "attempt",
            NpgsqlDbType.Text,
            _attempt.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "statement_timeout",
            NpgsqlDbType.Text,
            FormatTimeout(_options.StatementTimeout));
        command.Parameters.AddWithValue(
            "lock_timeout",
            NpgsqlDbType.Text,
            FormatTimeout(_options.LockTimeout));
        command.Parameters.AddWithValue(
            "idle_timeout",
            NpgsqlDbType.Text,
            FormatTimeout(_options.IdleInTransactionTimeout));
        command.Parameters.AddWithValue(
            "search_path",
            NpgsqlDbType.Text,
            BuildSearchPath(_searchPathSchema));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatTimeout(TimeSpan timeout) =>
        $"{(long)Math.Ceiling(timeout.TotalMilliseconds)}ms";
}

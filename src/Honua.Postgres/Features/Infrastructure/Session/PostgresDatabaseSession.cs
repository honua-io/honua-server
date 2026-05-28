// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Session;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseSession"/>. Owns the
/// underlying <see cref="DbConnection"/> (and optional <see cref="DbTransaction"/>)
/// and releases them on <see cref="DisposeAsync"/>.
/// </summary>
internal sealed class PostgresDatabaseSession : IDatabaseSession
{
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;
    private bool _committed;
    private bool _disposed;
    private readonly string _connectionString;

    public PostgresDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _connectionString = connection.ConnectionString ?? string.Empty;
    }

    public string ConnectionString => _connectionString;

    public bool IsTransactional => _transaction is not null;

    public async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var command = CreateCommand(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var command = CreateCommand(sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return ConvertScalar<T>(result);
    }

    public async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        object? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                yield return default!;
                continue;
            }

            var value = reader.GetValue(0);
            yield return ConvertScalar<T>(value)!;
        }
    }

    public async Task<IDatabaseSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        // Nested transactions on the same connection are not supported by Postgres
        // through ADO.NET directly; consumers requiring savepoints should use the
        // legacy provider for now. See ADR 0046.
        if (_transaction is not null)
        {
            throw new InvalidOperationException(
                "This session already has an active transaction. Nested transactions are not supported.");
        }

        var tx = await _connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        // Return a wrapper session that shares the connection but owns the transaction
        // lifetime. Disposing the wrapper will roll back if not committed.
        return new PostgresTransactionalSubSession(_connection, tx);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is null)
        {
            throw new InvalidOperationException("Cannot commit: session is not transactional.");
        }

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is null)
        {
            throw new InvalidOperationException("Cannot rollback: session is not transactional.");
        }

        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _committed = true; // mark to prevent double-rollback on dispose
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_transaction is not null)
        {
            try
            {
                if (!_committed)
                {
                    await _transaction.RollbackAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // best-effort rollback on dispose; do not mask original exception path
            }
            finally
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
                _transaction = null;
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private NpgsqlCommand CreateCommand(string sql, object? parameters)
    {
        var connection = _connection as NpgsqlConnection
            ?? throw new InvalidOperationException("PostgresDatabaseSession requires an NpgsqlConnection.");
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction is NpgsqlTransaction npgsqlTransaction)
        {
            command.Transaction = npgsqlTransaction;
        }

        ParameterBinder.Bind(command, parameters);
        return command;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static T? ConvertScalar<T>(object? value)
    {
        if (value is null || value is DBNull)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Sub-session created by <see cref="PostgresDatabaseSession.BeginTransactionAsync"/>.
/// Does NOT own the underlying connection (the parent session does); it owns
/// only the transaction.
/// </summary>
internal sealed class PostgresTransactionalSubSession : IDatabaseSession
{
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;
    private bool _committed;
    private bool _disposed;

    public PostgresTransactionalSubSession(DbConnection connection, DbTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public string ConnectionString => _connection.ConnectionString ?? string.Empty;

    public bool IsTransactional => _transaction is not null;

    public async Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var command = CreateCommand(sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var command = CreateCommand(sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return ConvertScalar<T>(result);
    }

    public async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        object? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false))
            {
                yield return default!;
                continue;
            }

            yield return ConvertScalar<T>(reader.GetValue(0))!;
        }
    }

    public Task<IDatabaseSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        throw new InvalidOperationException("Nested transactions are not supported on a transactional sub-session.");
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is null)
        {
            throw new InvalidOperationException("Cannot commit: session is not transactional.");
        }

        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is null)
        {
            throw new InvalidOperationException("Cannot rollback: session is not transactional.");
        }

        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_transaction is not null)
        {
            try
            {
                if (!_committed)
                {
                    await _transaction.RollbackAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // swallow rollback errors during dispose
            }
            finally
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
                _transaction = null;
            }
        }

        // Do NOT dispose the connection — that's the parent session's responsibility.
    }

    private NpgsqlCommand CreateCommand(string sql, object? parameters)
    {
        var connection = _connection as NpgsqlConnection
            ?? throw new InvalidOperationException("PostgresTransactionalSubSession requires an NpgsqlConnection.");
        var command = connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction is NpgsqlTransaction npgsqlTransaction)
        {
            command.Transaction = npgsqlTransaction;
        }

        ParameterBinder.Bind(command, parameters);
        return command;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static T? ConvertScalar<T>(object? value)
    {
        if (value is null || value is DBNull)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
    }
}

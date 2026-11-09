// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Core.Features.Infrastructure.Session;

public class AdoNetDatabaseSession : IDatabaseSession
{
    private readonly DbConnection _connection;
    private readonly bool _ownsConnection;
    private DbTransaction? _transaction;
    private bool _committed;
    private bool _disposed;
    private readonly string _connectionString;

    protected AdoNetDatabaseSession(DbConnection connection, DbTransaction? transaction, bool ownsConnection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _transaction = transaction;
        _ownsConnection = ownsConnection;
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

            yield return ConvertScalar<T>(reader.GetValue(0))!;
        }
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        Func<IDatabaseRow, T> rowMapper,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(rowMapper);

        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        return rowMapper(new DataReaderRow(reader));
    }

    public async IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        Func<IDatabaseRow, T> rowMapper,
        object? parameters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(rowMapper);

        await using var command = CreateCommand(sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var row = new DataReaderRow(reader);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return rowMapper(row);
        }
    }

    public async Task<IDatabaseSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is not null)
        {
            throw new InvalidOperationException(
                "This session already has an active transaction. Nested transactions are not supported.");
        }

        var tx = await _connection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new AdoNetTransactionalSubSession(_connection, tx);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_transaction is null)
        {
            throw new InvalidOperationException("Cannot commit: session is not transactional.");
        }

        // Guard against phantom commits: check the token before the COMMIT round-trip
        // so we fail cleanly if already cancelled; then commit with None so the in-flight
        // COMMIT can never be interrupted once it starts (see HN0001).
        cancellationToken.ThrowIfCancellationRequested();
        await _transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
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
            catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
            {
                // best-effort rollback on dispose; do not mask the original exception
            }
            finally
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
                _transaction = null;
            }
        }

        if (_ownsConnection)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private DbCommand CreateCommand(string sql, object? parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction is not null)
        {
            command.Transaction = _transaction;
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

    private sealed class DataReaderRow(DbDataReader reader) : IDatabaseRow
    {
        public int FieldCount => reader.FieldCount;

        public bool IsNull(int ordinal) => reader.IsDBNull(ordinal);

        public T GetFieldValue<T>(int ordinal) => reader.GetFieldValue<T>(ordinal);

        public int GetOrdinal(string name) => reader.GetOrdinal(name);
    }
}

internal sealed class AdoNetTransactionalSubSession : AdoNetDatabaseSession
{
    public AdoNetTransactionalSubSession(DbConnection connection, DbTransaction transaction)
        : base(connection, transaction, ownsConnection: false)
    {
    }
}

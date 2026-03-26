// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Wraps a database connection to release a semaphore when disposed.
/// Used by <see cref="Caching.CachingDatabaseConnectionProvider"/> to enforce
/// MaxConcurrentQueries — the semaphore is acquired before opening the connection
/// and released when the connection is returned (disposed).
/// </summary>
internal sealed class SemaphoreReleasingConnection : DbConnection
{
    private readonly NpgsqlConnection _inner;
    private readonly SemaphoreSlim _semaphore;
    private bool _disposed;

    public SemaphoreReleasingConnection(NpgsqlConnection inner, SemaphoreSlim semaphore)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
    }

    internal NpgsqlConnection InnerConnection => _inner;

    public override string ConnectionString
    {
        get => _inner.ConnectionString;
#pragma warning disable CS8765
        set => _inner.ConnectionString = value;
#pragma warning restore CS8765
    }

    public override string Database => _inner.Database;
    public override string DataSource => _inner.DataSource;
    public override string ServerVersion => _inner.ServerVersion;
    public override ConnectionState State => _inner.State;

    public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
    public override void Close() => _inner.Close();
    public override void Open() => _inner.Open();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => _inner.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand()
    {
        var cmd = _inner.CreateCommand();
        return cmd;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                _inner.Dispose();
                _semaphore.Release();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _inner.DisposeAsync().ConfigureAwait(false);
            _semaphore.Release();
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

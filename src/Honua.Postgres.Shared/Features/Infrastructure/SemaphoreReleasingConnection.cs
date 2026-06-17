// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Wraps a database connection to invoke a release callback when disposed.
/// Used by <see cref="Caching.CachingDatabaseConnectionProvider"/> to enforce
/// MaxConcurrentQueries — the gate slot is acquired before opening the connection
/// and released when the connection is returned (disposed).
/// </summary>
/// <remarks>
/// The wrapper relays <see cref="DbConnection.StateChange"/> events from the
/// inner <see cref="NpgsqlConnection"/> so that code subscribing to the wrapper
/// (for example
/// <see cref="Monitoring.DbConnectionTracking.Track"/>) sees Closed/Broken
/// transitions and can decrement active-connection telemetry. Without the
/// relay, a subscriber on the wrapper would never receive the close signal.
/// </remarks>
internal sealed class SemaphoreReleasingConnection : DbConnection
{
    private readonly NpgsqlConnection _inner;
    private readonly Action _releaseAction;
    private bool _disposed;

    public SemaphoreReleasingConnection(NpgsqlConnection inner, Action releaseAction)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _releaseAction = releaseAction ?? throw new ArgumentNullException(nameof(releaseAction));
        _inner.StateChange += OnInnerStateChange;
    }

    private void OnInnerStateChange(object? sender, StateChangeEventArgs e)
    {
        // Relay inner state transitions onto the wrapper's own StateChange
        // event so subscribers on the wrapper see the same lifecycle signals.
        OnStateChange(e);
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

    // Without this override, DbConnection.BeginTransactionAsync falls back to
    // calling the synchronous BeginDbTransaction above, turning every gated
    // OpenTransactionAsync into blocking I/O on the request thread. Forwarding
    // to NpgsqlConnection.BeginTransactionAsync preserves the async path.
    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken)
        => await _inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

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
                // Unsubscribe the relay BEFORE disposing the inner connection
                // so a subscriber throwing on the relayed StateChange(Closed)
                // cannot unwind out of Dispose and leak the gate slot. The
                // try/finally then guarantees _releaseAction runs exactly once
                // even if _inner.Dispose() itself throws.
                _inner.StateChange -= OnInnerStateChange;
                try
                {
                    _inner.Dispose();
                }
                finally
                {
                    _releaseAction();
                }
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            // Unsubscribe the relay BEFORE disposing the inner connection so a
            // subscriber throwing on the relayed StateChange(Closed) cannot
            // unwind out of DisposeAsync and leak the gate slot. The
            // try/finally then guarantees _releaseAction runs exactly once even
            // if _inner.DisposeAsync() itself throws.
            _inner.StateChange -= OnInnerStateChange;
            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _releaseAction();
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}

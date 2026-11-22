// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Styling;

// A transaction-scoped advisory lock: it is released when the transaction ends, so a crashed
// request cannot leave the target held on a pooled connection.
internal sealed class PostgresStyleSaveLock : IStyleSaveLock
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly string _scope;
    private readonly string _lockTimeout;

    // A short default wait: every waiter holds a pooled connection, and the holder needs several more to save.
    public PostgresStyleSaveLock(IAdoNetDatabaseConnectionProvider connectionProvider, string? schemaName = null, int waitMilliseconds = 2000)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        ArgumentOutOfRangeException.ThrowIfLessThan(waitMilliseconds, 1);
        _scope = "honua:style-save|" + (string.IsNullOrWhiteSpace(schemaName) ? "honua" : schemaName) + "|";
        _lockTimeout = waitMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(string targetKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        var lease = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (lease.Transaction is not null)
        {
            // Borrowing a caller's transaction would change its lock timeout and abort it on a wait timeout.
            throw new InvalidOperationException("A style save lock cannot be taken inside a mutation transaction.");
        }

        NpgsqlTransaction? transaction = null;
        try
        {
            transaction = await lease.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // SET cannot take a bind parameter; the value is an integer formatted above.
            await using var command = new NpgsqlCommand(
                $"SET LOCAL lock_timeout = '{_lockTimeout}'; SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));",
                lease.Connection,
                transaction);
            _ = command.Parameters.AddWithValue("@key", _scope + targetKey);
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new Held(lease, transaction);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            await ReleaseAsync(lease, transaction).ConfigureAwait(false);
            return null;
        }
        catch
        {
            await ReleaseAsync(lease, transaction).ConfigureAwait(false);
            throw;
        }
    }

    // Ending the transaction releases the advisory lock; it wrote nothing, so it rolls back.
    private static async ValueTask ReleaseAsync(NpgsqlConnectionLease lease, NpgsqlTransaction? transaction)
    {
        if (transaction is not null)
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }

        await lease.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class Held : IAsyncDisposable
    {
        private readonly NpgsqlConnectionLease _lease;
        private readonly NpgsqlTransaction _transaction;

        public Held(NpgsqlConnectionLease lease, NpgsqlTransaction transaction)
        {
            _lease = lease;
            _transaction = transaction;
        }

        public ValueTask DisposeAsync() => ReleaseAsync(_lease, _transaction);
    }
}

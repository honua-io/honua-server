// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure;

/// <summary>
/// Scoped lease over an open <see cref="NpgsqlConnection"/> returned from an
/// <see cref="IDatabaseConnectionProvider"/>. Preserves the original
/// <see cref="DbConnection"/> wrapper so that lifetime-bound side effects —
/// notably the <see cref="QueryConcurrencyGate"/> slot release implemented by
/// <see cref="SemaphoreReleasingConnection"/> — run when the caller disposes
/// the lease. Disposing only the inner <see cref="NpgsqlConnection"/> would
/// leak the gate slot for the remainder of the scoped provider's lifetime.
/// </summary>
/// <remarks>
/// Implicit conversion to <see cref="NpgsqlConnection"/> keeps existing call
/// sites — <c>new NpgsqlCommand(sql, connection)</c>, helper methods that take
/// an <see cref="NpgsqlConnection"/> parameter, etc. — working unchanged.
/// </remarks>
internal readonly struct NpgsqlConnectionLease : IAsyncDisposable
{
    private readonly DbConnection _owner;

    public NpgsqlConnectionLease(DbConnection owner, NpgsqlConnection connection)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>
    /// The underlying <see cref="NpgsqlConnection"/> usable with Npgsql-specific APIs.
    /// </summary>
    public NpgsqlConnection Connection { get; }

    /// <summary>
    /// Implicit conversion so existing call sites can continue passing the lease
    /// wherever an <see cref="NpgsqlConnection"/> is expected.
    /// </summary>
    public static implicit operator NpgsqlConnection(NpgsqlConnectionLease lease) => lease.Connection;

    /// <summary>
    /// Forwarder for <see cref="NpgsqlConnection.BeginTransactionAsync(CancellationToken)"/>
    /// so call sites can keep using <c>connection.BeginTransactionAsync(...)</c>.
    /// </summary>
    public ValueTask<NpgsqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Connection.BeginTransactionAsync(cancellationToken);

    /// <summary>
    /// Disposes the original wrapper so release callbacks (for example the
    /// query concurrency gate slot) run exactly once when the lease goes out
    /// of scope.
    /// </summary>
    public ValueTask DisposeAsync() => _owner.DisposeAsync();
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Infrastructure.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Features.Infrastructure;

// Disposing only the inner connection would skip the concurrency-gate release on the wrapper and leak the slot for the rest of the scope.
internal readonly struct NpgsqlConnectionLease : IAsyncDisposable
{
    private readonly DbConnection? _owner;

    public NpgsqlConnectionLease(DbConnection owner, NpgsqlConnection connection)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    internal NpgsqlConnectionLease(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _owner = null;
        Connection = connection;
        Transaction = transaction;
    }

    internal NpgsqlTransaction? Transaction { get; }

    public NpgsqlConnection Connection { get; }

    public static implicit operator NpgsqlConnection(NpgsqlConnectionLease lease) => lease.Connection;

    public ValueTask<NpgsqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Connection.BeginTransactionAsync(cancellationToken);

    public ValueTask<NpgsqlTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
        => Connection.BeginTransactionAsync(isolationLevel, cancellationToken);

    public ValueTask DisposeAsync() => _owner?.DisposeAsync() ?? ValueTask.CompletedTask;
}

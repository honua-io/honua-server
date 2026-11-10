// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;

namespace Honua.Core.Features.Infrastructure.Abstractions;

// Disposal without a commit rolls the transaction back, and the caller must dispose the session to return the connection.
public interface IDatabaseSession : IAsyncDisposable
{
    string ConnectionString { get; }

    bool IsTransactional { get; }

    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        Func<IDatabaseRow, T> rowMapper,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<T> QueryAsync<T>(
        string sql,
        Func<IDatabaseRow, T> rowMapper,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    Task<IDatabaseSession> BeginTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);

    Task CommitAsync(CancellationToken cancellationToken = default);

    Task RollbackAsync(CancellationToken cancellationToken = default);
}

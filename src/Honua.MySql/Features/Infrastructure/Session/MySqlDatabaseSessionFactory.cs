// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.MySql.Features.Infrastructure.Session;

/// <summary>
/// MySQL/MariaDB implementation of <see cref="IDatabaseSessionFactory"/>. Wraps
/// the existing <see cref="IDatabaseConnectionProvider"/> so resilience and
/// data-source pooling are reused.
/// </summary>
internal sealed class MySqlDatabaseSessionFactory : IDatabaseSessionFactory
{
    private readonly IDatabaseConnectionProvider _connectionProvider;

    public MySqlDatabaseSessionFactory(IDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new MySqlDatabaseSession(connection);
    }

    public async Task<IDatabaseSession> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var (connection, transaction) = await _connectionProvider
            .OpenTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);
        return new MySqlDatabaseSession(connection, transaction);
    }
}

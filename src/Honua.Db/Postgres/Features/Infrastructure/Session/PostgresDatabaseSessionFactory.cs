// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Db.Postgres.Features.Infrastructure.Session;

internal sealed class PostgresDatabaseSessionFactory : IDatabaseSessionFactory
{
    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;

    public PostgresDatabaseSessionFactory(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new PostgresDatabaseSession(connection);
    }

    public async Task<IDatabaseSession> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
    {
        var (connection, transaction) = await _connectionProvider
            .OpenTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);
        return new PostgresDatabaseSession(connection, transaction);
    }
}

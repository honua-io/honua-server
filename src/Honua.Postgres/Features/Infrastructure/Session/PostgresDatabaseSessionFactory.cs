// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.Postgres.Features.Infrastructure.Session;

/// <summary>
/// PostgreSQL implementation of <see cref="IDatabaseSessionFactory"/>.
/// Wraps <see cref="IAdoNetDatabaseConnectionProvider"/> internally so that the
/// existing resilience, concurrency-gate, schema-search-path, and tracking
/// behaviour is reused without duplication.
/// </summary>
/// <remarks>
/// During the audit C3 migration window (ADR 0046) both the legacy provider
/// and the new factory are registered in DI; consumers migrate progressively
/// from <see cref="IAdoNetDatabaseConnectionProvider"/> to
/// <see cref="IDatabaseSessionFactory"/>.
/// </remarks>
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

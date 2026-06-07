// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Honua.SqlServer.Features.FeatureStore.Services;

/// <summary>
/// Resolves and opens a <see cref="SqlConnection"/> for a SQL Server feature operation.
/// </summary>
internal interface ISqlServerConnectionFactory
{
    /// <summary>
    /// Opens a SQL Server connection using the supplied <see cref="DataConnection"/> when
    /// available, otherwise falling back to the configured default.
    /// </summary>
    Task<SqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ISqlServerConnectionFactory"/> implementation. Pooling is handled by
/// <see cref="SqlConnection"/> internally; the factory just hands out new connections.
/// </summary>
internal sealed class SqlServerConnectionFactory : ISqlServerConnectionFactory
{
    private readonly IOptions<SqlServerOptions> _options;
    private readonly ISecureConnectionResolver? _connectionResolver;

    public SqlServerConnectionFactory(
        IOptions<SqlServerOptions> options,
        ISecureConnectionResolver? connectionResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionResolver = connectionResolver;
    }

    public async Task<SqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
    {
        var connectionString = await ResolveConnectionStringAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No SQL Server connection string is configured. Set 'SqlServer:ConnectionString' or attach a secure DataConnection to the service.");
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string?> ResolveConnectionStringAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
    {
        // Prefer a usable inline plaintext connection string so it is never routed to the secure
        // resolver. A registry-backed connection (encrypted bytes / secret reference) has no usable
        // inline string and must be resolved by ID through ISecureConnectionResolver.
        if (dataConnection is { ConnectionString.Length: > 0 } provided && !provided.IsEncrypted)
        {
            return provided.ConnectionString;
        }

        if (dataConnection is not null && IsRegistryBacked(dataConnection))
        {
            // ConnectionId guards against default-constructed connections being routed to the
            // resolver: although the domain default is Guid.NewGuid(), a connection materialized
            // without an assigned identity may carry Guid.Empty, which the resolver cannot look up.
            if (dataConnection.ConnectionId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "A registry-backed SQL Server DataConnection was supplied without a ConnectionId; " +
                    "the secure connection cannot be resolved.");
            }

            if (_connectionResolver is null)
            {
                // Fail closed: a secret/encrypted connection must never silently fall back to the
                // global default database, which could route the query to the wrong database.
                throw new InvalidOperationException(
                    "A secure (registry-backed) SQL Server DataConnection was requested but no " +
                    "ISecureConnectionResolver is registered to resolve it.");
            }

            return await _connectionResolver.ResolveConnectionStringAsync(dataConnection.ConnectionId, cancellationToken).ConfigureAwait(false);
        }

        return _options.Value.ConnectionString;
    }

    /// <summary>
    /// Determines whether the connection's credentials live in the secure registry (encrypted bytes
    /// or an external secret reference) rather than being available as inline plaintext.
    /// </summary>
    private static bool IsRegistryBacked(DataConnection dataConnection)
        => dataConnection.IsEncrypted
            || dataConnection.ConnectionStringEncrypted is { Length: > 0 }
            || !string.IsNullOrWhiteSpace(dataConnection.SecretRef);
}

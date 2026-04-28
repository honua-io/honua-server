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
        if (dataConnection is { ConnectionId: var id } && id != Guid.Empty && _connectionResolver is not null)
        {
            return await _connectionResolver.ResolveConnectionStringAsync(id, cancellationToken).ConfigureAwait(false);
        }

        if (dataConnection is { ConnectionString.Length: > 0 } provided && !provided.IsEncrypted)
        {
            return provided.ConnectionString;
        }

        return _options.Value.ConnectionString;
    }
}

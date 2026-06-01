// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace Honua.Oracle.Features.FeatureStore.Services;

/// <summary>
/// Resolves and opens an <see cref="OracleConnection"/> for an Oracle feature operation.
/// </summary>
internal interface IOracleConnectionFactory
{
    /// <summary>
    /// Opens an Oracle connection using the supplied <see cref="DataConnection"/> when
    /// available, otherwise falling back to the configured default.
    /// </summary>
    Task<OracleConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IOracleConnectionFactory"/> implementation. Pooling is handled by
/// <see cref="OracleConnection"/> internally; the factory just hands out new connections.
/// </summary>
internal sealed class OracleConnectionFactory : IOracleConnectionFactory
{
    private readonly IOptions<OracleOptions> _options;
    private readonly ISecureConnectionResolver? _connectionResolver;

    public OracleConnectionFactory(
        IOptions<OracleOptions> options,
        ISecureConnectionResolver? connectionResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionResolver = connectionResolver;
    }

    public async Task<OracleConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
    {
        var connectionString = await ResolveConnectionStringAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No Oracle connection string is configured. Set 'Oracle:ConnectionString' or attach a secure DataConnection to the service.");
        }

        var connection = new OracleConnection(connectionString);
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

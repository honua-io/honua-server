// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Snowflake.Data.Client;

namespace Honua.Snowflake.Features.FeatureStore.Services;

/// <summary>
/// Resolves and opens a Snowflake <see cref="DbConnection"/> for a feature operation.
/// </summary>
internal interface ISnowflakeConnectionFactory
{
    /// <summary>
    /// Opens a Snowflake connection using the supplied <see cref="DataConnection"/> when available,
    /// otherwise falling back to the configured default connection string.
    /// </summary>
    Task<DbConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ISnowflakeConnectionFactory"/> implementation. Connection pooling is handled
/// by <c>Snowflake.Data</c> internally; the factory just hands out freshly opened connections.
/// </summary>
internal sealed class SnowflakeConnectionFactory : ISnowflakeConnectionFactory
{
    private readonly IOptions<SnowflakeOptions> _options;
    private readonly ISecureConnectionResolver? _connectionResolver;

    public SnowflakeConnectionFactory(
        IOptions<SnowflakeOptions> options,
        ISecureConnectionResolver? connectionResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionResolver = connectionResolver;
    }

    public async Task<DbConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
    {
        var connectionString = await ResolveConnectionStringAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No Snowflake connection string is configured. Set 'Snowflake:ConnectionString' or attach a secure DataConnection to the service.");
        }

        var connection = new SnowflakeDbConnection { ConnectionString = connectionString };
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

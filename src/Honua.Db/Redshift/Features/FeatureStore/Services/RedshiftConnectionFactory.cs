// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Redshift.Features.FeatureStore.Services;

/// <summary>
/// Resolves and opens an <see cref="NpgsqlConnection"/> for a Redshift feature operation.
/// </summary>
internal interface IRedshiftConnectionFactory
{
    /// <summary>
    /// Opens a Redshift connection using the supplied <see cref="DataConnection"/> when
    /// available, otherwise falling back to the configured default.
    /// </summary>
    Task<NpgsqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IRedshiftConnectionFactory"/> implementation. Npgsql handles pooling
/// internally; the factory just hands out new, opened connections. Redshift speaks the
/// PostgreSQL wire protocol, so the Npgsql driver connects without modification.
/// </summary>
internal sealed class RedshiftConnectionFactory : IRedshiftConnectionFactory
{
    private readonly IOptions<RedshiftOptions> _options;
    private readonly ISecureConnectionResolver? _connectionResolver;

    public RedshiftConnectionFactory(
        IOptions<RedshiftOptions> options,
        ISecureConnectionResolver? connectionResolver = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionResolver = connectionResolver;
    }

    public async Task<NpgsqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
    {
        var connectionString = await ResolveConnectionStringAsync(dataConnection, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "No Redshift connection string is configured. Set 'Redshift:ConnectionString' or attach a secure DataConnection to the service.");
        }

        var connection = new NpgsqlConnection(connectionString);
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

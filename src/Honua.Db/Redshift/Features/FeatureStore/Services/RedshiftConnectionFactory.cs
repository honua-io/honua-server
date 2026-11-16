// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Db.Redshift.Features.FeatureStore.Services;

internal interface IRedshiftConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

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

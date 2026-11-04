// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Options;
using Snowflake.Data.Client;

namespace Honua.Snowflake.Features.FeatureStore.Services;

internal interface ISnowflakeConnectionFactory
{
    Task<DbConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken);
}

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

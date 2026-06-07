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
        // No per-layer connection: use the configured global default.
        if (dataConnection is null)
        {
            return _options.Value.ConnectionString;
        }

        // A per-layer DataConnection was supplied. It must resolve to that layer's intended
        // connection; we must NOT fall back to the global default, which would silently route the
        // read to the wrong (possibly unsecured) database. Fail loudly on any unresolvable case.
        if (dataConnection.ConnectionId != Guid.Empty)
        {
            if (_connectionResolver is null)
            {
                throw new InvalidOperationException(
                    $"Oracle DataConnection '{dataConnection.ConnectionId}' requires secure resolution, but no ISecureConnectionResolver is registered. " +
                    "Register a resolver instead of allowing a fallback to the default Oracle connection.");
            }

            var resolved = await _connectionResolver.ResolveConnectionStringAsync(dataConnection.ConnectionId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                throw new InvalidOperationException(
                    $"Oracle DataConnection '{dataConnection.ConnectionId}' resolved to an empty connection string. " +
                    "Refusing to fall back to the default Oracle connection to avoid routing the read to the wrong database.");
            }

            return resolved;
        }

        // ConnectionId is empty, so the only usable source is the inline plaintext string.
        // An encrypted connection with no ConnectionId cannot be decrypted here and must not be
        // silently downgraded to the global default.
        if (dataConnection.IsEncrypted)
        {
            throw new InvalidOperationException(
                "Oracle DataConnection is encrypted but carries no ConnectionId for secure resolution. " +
                "Refusing to fall back to the default Oracle connection to avoid routing the read to the wrong database.");
        }

        if (string.IsNullOrWhiteSpace(dataConnection.ConnectionString))
        {
            throw new InvalidOperationException(
                "Oracle DataConnection carries neither a ConnectionId nor a usable plaintext connection string. " +
                "Refusing to fall back to the default Oracle connection to avoid routing the read to the wrong database.");
        }

        return dataConnection.ConnectionString;
    }
}

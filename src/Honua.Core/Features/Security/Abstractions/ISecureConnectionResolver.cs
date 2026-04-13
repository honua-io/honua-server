// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Service for resolving secure database connection strings from registered connections.
/// </summary>
public interface ISecureConnectionResolver
{
    /// <summary>
    /// Resolves a connection string by name.
    /// </summary>
    /// <param name="connectionName">The connection name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved connection string</returns>
    Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a connection string by ID.
    /// </summary>
    /// <param name="connectionId">The connection ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The resolved connection string</returns>
    Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the health of a connection.
    /// </summary>
    /// <param name="connectionName">The connection name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the connection is healthy</returns>
    Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available connection names.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available connection names</returns>
    Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Registry for managing secure database connections.
/// </summary>
public interface ISecureConnectionRegistry
{
    /// <summary>
    /// Creates a new data connection.
    /// </summary>
    /// <param name="connection">The connection to create.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created connection.</returns>
    Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a new data connection.
    /// </summary>
    /// <param name="connection">The connection to register</param>
    /// <returns>Task representing the async operation</returns>
    Task RegisterConnectionAsync(DataConnection connection);

    /// <summary>
    /// Gets a connection by ID.
    /// </summary>
    /// <param name="connectionId">The connection ID</param>
    /// <returns>The data connection if found</returns>
    Task<DataConnection?> GetConnectionAsync(string connectionId);

    /// <summary>
    /// Gets a connection by GUID identifier.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The data connection if found.</returns>
    Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered connections.
    /// </summary>
    /// <returns>List of all connections</returns>
    Task<IEnumerable<DataConnection>> GetAllConnectionsAsync();

    /// <summary>
    /// Removes a connection by ID.
    /// </summary>
    /// <param name="connectionId">The connection ID to remove</param>
    /// <returns>True if removed, false if not found</returns>
    Task<bool> RemoveConnectionAsync(string connectionId);

    /// <summary>
    /// Deletes a connection by GUID identifier.
    /// </summary>
    /// <param name="connectionId">The connection ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted; otherwise false.</returns>
    Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the health of all connections.
    /// </summary>
    /// <returns>Dictionary of connection IDs to health status</returns>
    Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync();

    /// <summary>
    /// Gets a connection by name.
    /// </summary>
    /// <param name="connectionName">The connection name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The data connection if found</returns>
    Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a connection by ID with cancellation support.
    /// </summary>
    /// <param name="connectionId">The connection ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The data connection if found</returns>
    Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the health status of a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID</param>
    /// <param name="isHealthy">Whether the connection is healthy</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active connections.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active connections</returns>
    Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing data connection.
    /// </summary>
    /// <param name="connection">The updated connection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated connection.</returns>
    Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Security.Abstractions;

/// <summary>
/// Repository interface for secure database connection configurations.
/// </summary>
/// <remarks>
/// Provides CRUD operations for encrypted database connection configurations.
/// </remarks>
public interface ISecureConnectionRegistry
{
    /// <summary>
    /// Creates a new secure database connection configuration.
    /// </summary>
    /// <param name="connection">Connection configuration to create</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created connection configuration</returns>
    /// <exception cref="ArgumentNullException">Thrown when connection is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when connection name already exists or validation fails</exception>
    Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a connection configuration by ID.
    /// </summary>
    /// <param name="connectionId">Connection ID to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection configuration if found, null otherwise</returns>
    Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a connection configuration by name.
    /// </summary>
    /// <param name="name">Connection name to retrieve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection configuration if found, null otherwise</returns>
    Task<DataConnection?> GetConnectionByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all active connection configurations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active connection configurations</returns>
    Task<IReadOnlyList<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing connection configuration.
    /// </summary>
    /// <param name="connection">Updated connection configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The updated connection configuration</returns>
    /// <exception cref="ArgumentNullException">Thrown when connection is null</exception>
    /// <exception cref="InvalidOperationException">Thrown when connection not found or validation fails</exception>
    Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a connection configuration.
    /// </summary>
    /// <param name="connectionId">Connection ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found</returns>
    Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests connectivity for a connection configuration.
    /// </summary>
    /// <param name="connectionId">Connection ID to test</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection is successful, false otherwise</returns>
    Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the health status of a connection.
    /// </summary>
    /// <param name="connectionId">Connection ID to update</param>
    /// <param name="healthStatus">New health status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if updated, false if connection not found</returns>
    Task<bool> UpdateHealthStatusAsync(Guid connectionId, ConnectionHealthStatus healthStatus, CancellationToken cancellationToken = default);

}

/// <summary>
/// Service interface for resolving actual database connections from the secure registry.
/// </summary>
/// <remarks>
/// This service combines the secure registry with encryption/secret resolution
/// to provide actual database connection strings for use by the application.
/// </remarks>
public interface ISecureConnectionResolver
{
    /// <summary>
    /// Resolves a database connection string from the secure registry.
    /// </summary>
    /// <param name="connectionName">Name of the connection to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Resolved plaintext connection string</returns>
    /// <exception cref="ArgumentException">Thrown when connectionName is null or empty</exception>
    /// <exception cref="InvalidOperationException">Thrown when connection not found or inactive</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when connection access is denied</exception>
    Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a database connection string by connection ID.
    /// </summary>
    /// <param name="connectionId">ID of the connection to resolve</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Resolved plaintext connection string</returns>
    /// <exception cref="InvalidOperationException">Thrown when connection not found or inactive</exception>
    /// <exception cref="UnauthorizedAccessException">Thrown when connection access is denied</exception>
    Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests whether a connection can be resolved and is healthy.
    /// </summary>
    /// <param name="connectionName">Name of the connection to test</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection can be resolved and is reachable</returns>
    Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of available connection names for discovery.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of available connection names</returns>
    Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default);
}

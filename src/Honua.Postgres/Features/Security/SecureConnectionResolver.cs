// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// Secure connection resolver that combines the registry, encryption, and secret resolution
/// to provide actual database connection strings for application use.
/// </summary>
/// <remarks>
/// This service provides the primary interface for resolving database connections
/// while maintaining security. All operations are logged and include comprehensive error handling.
/// </remarks>
internal sealed class SecureConnectionResolver : ISecureConnectionResolver
{
    private static readonly Action<ILogger, string, Exception?> _logConnectionNotFound =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1001), "Connection configuration '{ConnectionName}' not found");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionNotFoundById =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(1002), "Connection configuration with ID {ConnectionId} not found");

    private static readonly Action<ILogger, string, Exception?> _logConnectionNotFoundOrInactive =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1003), "Connection '{ConnectionName}' not found or inactive");

    private static readonly Action<ILogger, string, Exception?> _logConnectionStringResolveFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1005), "Failed to resolve connection string for health check of '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logHealthCheckError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1006), "Unexpected error during health check for connection '{ConnectionName}'");

    private static readonly Action<ILogger, int, Exception?> _logAvailableConnectionsRetrieved =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(1007), "Retrieved {Count} available connection names");

    private static readonly Action<ILogger, Exception?> _logAvailableConnectionsError =
        LoggerMessage.Define(LogLevel.Error, new EventId(1008), "Failed to retrieve available connections");

    private static readonly Action<ILogger, string, Exception?> _logInactiveConnectionAttempt =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(1009), "Attempted to resolve inactive connection '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logConnectionStringDecrypted =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1011), "Successfully decrypted connection string for '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logConnectionStringResolvedFromSecret =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1012), "Successfully resolved connection string from secret reference for '{ConnectionName}'");

    private static readonly Action<ILogger, string, string, string, Exception?> _logConnectionHostMismatch =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(1013), "Connection string host '{ResolvedHost}' differs from configured host '{ConfiguredHost}' for connection '{ConnectionName}'");

    private static readonly Action<ILogger, int, int, string, Exception?> _logConnectionPortMismatch =
        LoggerMessage.Define<int, int, string>(LogLevel.Warning, new EventId(1014), "Connection string port {ResolvedPort} differs from configured port {ConfiguredPort} for connection '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logConnectionStringResolved =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1015), "Successfully resolved connection string for '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logConnectionStringResolveFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1016), "Failed to resolve connection string for '{ConnectionName}'");

    private static readonly Action<ILogger, string, Exception?> _logHealthCheckSuccess =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1017), "Health check successful for connection '{ConnectionName}'");

    private static readonly Action<ILogger, string, object?, Exception?> _logHealthCheckUnexpectedResult =
        LoggerMessage.Define<string, object?>(LogLevel.Warning, new EventId(1018), "Health check query returned unexpected result for connection '{ConnectionName}': {Result}");

    private static readonly Action<ILogger, string, string, Exception?> _logHealthCheckFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(1019), "Health check failed for connection '{ConnectionName}': {Error}");

    private readonly ISecureConnectionRegistry _registry;
    private readonly IConnectionEncryptionService _encryptionService;
    private readonly IConnectionSecretResolver _secretResolver;
    private readonly ILogger<SecureConnectionResolver> _logger;

    public SecureConnectionResolver(
        ISecureConnectionRegistry registry,
        IConnectionEncryptionService encryptionService,
        IConnectionSecretResolver secretResolver,
        ILogger<SecureConnectionResolver> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        _secretResolver = secretResolver ?? throw new ArgumentNullException(nameof(secretResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            throw new ArgumentException("Connection name cannot be null or empty", nameof(connectionName));

        var connection = await _registry.GetConnectionByNameAsync(connectionName, cancellationToken);
        if (connection == null)
        {
            _logConnectionNotFound(_logger, connectionName, null);
            throw new ResourceNotFoundException($"Connection configuration '{connectionName}' not found");
        }

        return await ResolveConnectionStringInternalAsync(connection, cancellationToken);
    }

    public async Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await _registry.GetConnectionAsync(connectionId, cancellationToken);
        if (connection == null)
        {
            _logConnectionNotFoundById(_logger, connectionId, null);
            throw new ResourceNotFoundException($"Connection configuration with ID {connectionId} not found");
        }

        return await ResolveConnectionStringInternalAsync(connection, cancellationToken);
    }

    public async Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionName))
            throw new ArgumentException("Connection name cannot be null or empty", nameof(connectionName));

        try
        {
            var connection = await _registry.GetConnectionByNameAsync(connectionName, cancellationToken);
            if (connection == null || !connection.IsActive)
            {
                _logConnectionNotFoundOrInactive(_logger, connectionName, null);
                return false;
            }

            // Attempt to resolve and test the actual connection
            try
            {
                var connectionString = await ResolveConnectionStringInternalAsync(connection, cancellationToken);
                return await TestActualConnectionAsync(connectionString, connection, cancellationToken);
            }
            catch (Exception ex)
            {
                _logConnectionStringResolveFailed(_logger, connectionName, ex);

                await _registry.UpdateHealthStatusAsync(connection.ConnectionId, ConnectionHealthStatus.Unhealthy, cancellationToken);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logHealthCheckError(_logger, connectionName, ex);
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connections = await _registry.GetActiveConnectionsAsync(cancellationToken);
            var connectionNames = connections.Select(c => c.Name).ToArray();

            _logAvailableConnectionsRetrieved(_logger, connectionNames.Length, null);
            return connectionNames;
        }
        catch (Exception ex)
        {
            _logAvailableConnectionsError(_logger, ex);
            throw;
        }
    }

    /// <summary>
    /// Internal method to resolve connection strings with comprehensive error handling.
    /// </summary>
    /// <param name="connection">Connection configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Resolved plaintext connection string</returns>
    private async Task<string> ResolveConnectionStringInternalAsync(DataConnection connection, CancellationToken cancellationToken)
    {
        // Check if connection is active
        if (!connection.IsActive)
        {
            _logInactiveConnectionAttempt(_logger, connection.Name, null);

            throw new InvalidOperationException($"Connection '{connection.Name}' is inactive");
        }

        try
        {
            string connectionString;

            // Resolve based on storage type
            if (connection.ConnectionStringEncrypted != null)
            {
                // Decrypt stored connection string
                connectionString = await _encryptionService.DecryptConnectionStringAsync(
                    connection.ConnectionStringEncrypted,
                    connection.EncryptionKeyVersion);

                _logConnectionStringDecrypted(_logger, connection.Name, null);
            }
            else if (!string.IsNullOrWhiteSpace(connection.SecretRef))
            {
                // Resolve from external secret manager
                connectionString = await _secretResolver.ResolveConnectionStringAsync(connection.SecretRef, cancellationToken);

                _logConnectionStringResolvedFromSecret(_logger, connection.Name, null);
            }
            else
            {
                throw new InvalidOperationException($"Connection '{connection.Name}' has neither encrypted credentials nor secret reference");
            }

            // Validate resolved connection string
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Resolved connection string for '{connection.Name}' is null or empty");
            }

            // Parse and validate connection string format
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(connectionString);

                // Verify SSL requirements are met
                if (connection.SslRequired && builder.SslMode == Npgsql.SslMode.Disable)
                {
                    throw new InvalidOperationException($"Connection '{connection.Name}' requires SSL but resolved connection string disables SSL");
                }

                // Additional validation: ensure host/port match expected values
                if (!string.IsNullOrWhiteSpace(builder.Host) && !string.Equals(builder.Host, connection.Host, StringComparison.OrdinalIgnoreCase))
                {
                    _logConnectionHostMismatch(_logger, builder.Host, connection.Host, connection.Name, null);
                }

                if (builder.Port != 0 && builder.Port != connection.Port)
                {
                    _logConnectionPortMismatch(_logger, builder.Port, connection.Port, connection.Name, null);
                }
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    $"Invalid connection string format for '{connection.Name}'.",
                    ex);
            }

            _logConnectionStringResolved(_logger, connection.Name, null);
            return connectionString;
        }
        catch (InvalidOperationException ex)
        {
            _logConnectionStringResolveFailure(_logger, connection.Name, ex);
            throw new InvalidOperationException(
                $"Failed to resolve connection string for '{connection.Name}'.",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logConnectionStringResolveFailure(_logger, connection.Name, ex);

            throw new InvalidOperationException(
                $"Failed to resolve connection string for '{connection.Name}'.",
                ex);
        }
    }

    /// <summary>
    /// Tests an actual database connection to verify connectivity.
    /// </summary>
    /// <param name="connectionString">Connection string to test</param>
    /// <param name="connection">Connection configuration for logging</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if connection is successful</returns>
    private async Task<bool> TestActualConnectionAsync(string connectionString, DataConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var testConnection = new NpgsqlConnection(connectionString);

            await testConnection.OpenAsync(cancellationToken);

            // Perform a simple query to verify the connection works
            using var command = testConnection.CreateCommand();
            command.CommandText = "SELECT 1";

            // Set a short timeout for health checks
            command.CommandTimeout = 5;

            var result = await command.ExecuteScalarAsync(cancellationToken);

            var isHealthy = result?.ToString() == "1";

            if (isHealthy)
            {
                _logHealthCheckSuccess(_logger, connection.Name, null);
                await _registry.UpdateHealthStatusAsync(connection.ConnectionId, ConnectionHealthStatus.Healthy, cancellationToken);
            }
            else
            {
                _logHealthCheckUnexpectedResult(_logger, connection.Name, result, null);

                await _registry.UpdateHealthStatusAsync(connection.ConnectionId, ConnectionHealthStatus.Unhealthy, cancellationToken);
            }

            return isHealthy;
        }
        catch (Exception ex)
        {
            _logHealthCheckFailed(_logger, connection.Name, ex.GetType().Name, ex);

            await _registry.UpdateHealthStatusAsync(connection.ConnectionId, ConnectionHealthStatus.Unhealthy, cancellationToken);

            return false;
        }
    }
}

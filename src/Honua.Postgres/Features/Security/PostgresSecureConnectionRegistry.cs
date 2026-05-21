// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Postgres.Features.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Postgres.Features.Security;

/// <summary>
/// PostgreSQL implementation of the secure connection registry.
/// </summary>
/// <remarks>
/// Stores encrypted database connection configurations in PostgreSQL with validation.
/// </remarks>
internal sealed class PostgresSecureConnectionRegistry : ISecureConnectionRegistry
{
    private readonly IPrimaryDatabaseConnectionProvider _connectionProvider;
    private readonly ILogger<PostgresSecureConnectionRegistry> _logger;

    private static string ToLowerString(ConnectionHealthStatus status) => status switch
    {
        ConnectionHealthStatus.Unknown => "unknown",
        ConnectionHealthStatus.Healthy => "healthy",
        ConnectionHealthStatus.Unhealthy => "unhealthy",
        _ => status.ToString().ToLowerInvariant()
    };

    private static string ToLowerString(CoreSslMode mode) => mode switch
    {
        CoreSslMode.Disable => "disable",
        CoreSslMode.Allow => "allow",
        CoreSslMode.Prefer => "prefer",
        CoreSslMode.Require => "require",
        CoreSslMode.VerifyCa => "verify-ca",
        CoreSslMode.VerifyFull => "verify-full",
        _ => mode.ToString().ToLowerInvariant()
    };

    // Logger message delegates for performance
    private static readonly Action<ILogger, string, Guid, Exception?> _logConnectionCreated =
        LoggerMessage.Define<string, Guid>(LogLevel.Information, new EventId(1, nameof(_logConnectionCreated)),
            "Created secure connection configuration '{Name}' with ID {ConnectionId}");

    private static readonly Action<ILogger, string, Exception?> _logConnectionNameExists =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(2, nameof(_logConnectionNameExists)),
            "Failed to create connection '{Name}' - name already exists");

    private static readonly Action<ILogger, string, Exception?> _logConnectionCreationFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(3, nameof(_logConnectionCreationFailure)),
            "Failed to create secure connection configuration '{Name}'");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionRetrievalFailure =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(4, nameof(_logConnectionRetrievalFailure)),
            "Failed to retrieve connection configuration with ID {ConnectionId}");

    private static readonly Action<ILogger, string, Exception?> _logConnectionNameRetrievalFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(5, nameof(_logConnectionNameRetrievalFailure)),
            "Failed to retrieve connection configuration '{Name}'");

    private static readonly Action<ILogger, int, Exception?> _logConnectionsRetrieved =
        LoggerMessage.Define<int>(LogLevel.Debug, new EventId(6, nameof(_logConnectionsRetrieved)),
            "Retrieved {Count} active connection configurations");

    private static readonly Action<ILogger, Exception?> _logConnectionsRetrievalFailure =
        LoggerMessage.Define(LogLevel.Error, new EventId(7, nameof(_logConnectionsRetrievalFailure)),
            "Failed to retrieve active connection configurations");

    private static readonly Action<ILogger, string, Guid, Exception?> _logConnectionUpdated =
        LoggerMessage.Define<string, Guid>(LogLevel.Information, new EventId(8, nameof(_logConnectionUpdated)),
            "Updated secure connection configuration '{Name}' with ID {ConnectionId}");

    private static readonly Action<ILogger, string, Exception?> _logConnectionUpdateFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(9, nameof(_logConnectionUpdateFailure)),
            "Failed to update secure connection configuration '{Name}'");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionDeleted =
        LoggerMessage.Define<Guid>(LogLevel.Information, new EventId(10, nameof(_logConnectionDeleted)),
            "Deleted secure connection configuration with ID {ConnectionId}");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionNotFoundForDeletion =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(11, nameof(_logConnectionNotFoundForDeletion)),
            "No connection configuration found with ID {ConnectionId} for deletion");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionDeletionFailure =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(12, nameof(_logConnectionDeletionFailure)),
            "Failed to delete connection configuration with ID {ConnectionId}");

    private static readonly Action<ILogger, Guid, Exception?> _logConnectionNotFoundForTest =
        LoggerMessage.Define<Guid>(LogLevel.Warning, new EventId(13, nameof(_logConnectionNotFoundForTest)),
            "Cannot test connection - configuration with ID {ConnectionId} not found");

    private static readonly Action<ILogger, Guid, string, Exception?> _logHealthStatusUpdated =
        LoggerMessage.Define<Guid, string>(LogLevel.Debug, new EventId(15, nameof(_logHealthStatusUpdated)),
            "Updated health status for connection {ConnectionId} to {HealthStatus}");

    private static readonly Action<ILogger, Guid, Exception?> _logHealthStatusUpdateFailure =
        LoggerMessage.Define<Guid>(LogLevel.Error, new EventId(16, nameof(_logHealthStatusUpdateFailure)),
            "Failed to update health status for connection {ConnectionId}");

    public PostgresSecureConnectionRegistry(
        IPrimaryDatabaseConnectionProvider connectionProvider,
        ILogger<PostgresSecureConnectionRegistry> logger)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.Validate();

        const string sql = """
            INSERT INTO honua.data_connections (
                connection_id, name, description, host, port, database_name, username,
                provider_name,
                ssl_required, ssl_mode, connection_string_encrypted, encryption_key_version,
                secret_ref, secret_type, created_by, is_active
            ) VALUES (
                @connection_id, @name, @description, @host, @port, @database_name, @username,
                @provider_name,
                @ssl_required, @ssl_mode, @connection_string_encrypted, @encryption_key_version,
                @secret_ref, @secret_type, @created_by, @is_active
            )
            """;

        try
        {
            await using var dbConnection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = dbConnection.CreateCommand();

            command.CommandText = sql;
            AddConnectionParameters(command, connection);

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logConnectionCreated(_logger, connection.Name, connection.ConnectionId, null);

            return connection;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            _logConnectionNameExists(_logger, connection.Name, null);
            throw new InvalidOperationException($"Connection name '{connection.Name}' already exists", ex);
        }
        catch (Exception ex)
        {
            _logConnectionCreationFailure(_logger, connection.Name, ex);
            throw;
        }
    }

    public async Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT connection_id, name, description, host, port, database_name, username,
                   provider_name, ssl_required, ssl_mode, connection_string_encrypted, encryption_key_version,
                   secret_ref, secret_type, created_at, updated_at, created_by, is_active,
                   last_health_check, health_status
            FROM honua.data_connections
            WHERE connection_id = @connection_id
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@connection_id", connectionId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadDataConnection(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logConnectionRetrievalFailure(_logger, connectionId, ex);
            throw;
        }
    }

    public async Task<DataConnection?> GetConnectionByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Connection name cannot be null or empty", nameof(name));

        const string sql = """
            SELECT connection_id, name, description, host, port, database_name, username,
                   provider_name, ssl_required, ssl_mode, connection_string_encrypted, encryption_key_version,
                   secret_ref, secret_type, created_at, updated_at, created_by, is_active,
                   last_health_check, health_status
            FROM honua.data_connections
            WHERE name = @name
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@name", name));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (await reader.ReadAsync(cancellationToken))
            {
                return ReadDataConnection(reader);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logConnectionNameRetrievalFailure(_logger, name, ex);
            throw;
        }
    }

    public async Task<IReadOnlyList<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT connection_id, name, description, host, port, database_name, username,
                   provider_name, ssl_required, ssl_mode, connection_string_encrypted, encryption_key_version,
                   secret_ref, secret_type, created_at, updated_at, created_by, is_active,
                   last_health_check, health_status
            FROM honua.data_connections
            WHERE is_active = true
            ORDER BY name
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = sql;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var connections = new List<DataConnection>();

            while (await reader.ReadAsync(cancellationToken))
            {
                connections.Add(ReadDataConnection(reader));
            }

            _logConnectionsRetrieved(_logger, connections.Count, null);
            return connections.AsReadOnly();
        }
        catch (Exception ex)
        {
            _logConnectionsRetrievalFailure(_logger, ex);
            throw;
        }
    }

    public async Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.Validate();

        const string sql = """
            UPDATE honua.data_connections SET
                description = @description,
                host = @host,
                port = @port,
                database_name = @database_name,
                username = @username,
                provider_name = @provider_name,
                ssl_required = @ssl_required,
                ssl_mode = @ssl_mode,
                connection_string_encrypted = @connection_string_encrypted,
                encryption_key_version = @encryption_key_version,
                secret_ref = @secret_ref,
                secret_type = @secret_type,
                is_active = @is_active,
                updated_at = NOW()
            WHERE connection_id = @connection_id
            """;

        try
        {
            await using var dbConnection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = dbConnection.CreateCommand();

            command.CommandText = sql;
            AddConnectionParameters(command, connection);

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            if (rowsAffected == 0)
            {
                throw new InvalidOperationException($"Connection with ID {connection.ConnectionId} not found for update");
            }

            _logConnectionUpdated(_logger, connection.Name, connection.ConnectionId, null);

            return connection;
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            _logConnectionUpdateFailure(_logger, connection.Name, ex);
            throw;
        }
    }

    public async Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM honua.data_connections WHERE connection_id = @connection_id";

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@connection_id", connectionId));

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            if (rowsAffected > 0)
            {
                _logConnectionDeleted(_logger, connectionId, null);
                return true;
            }

            _logConnectionNotFoundForDeletion(_logger, connectionId, null);
            return false;
        }
        catch (Exception ex)
        {
            _logConnectionDeletionFailure(_logger, connectionId, ex);
            throw;
        }
    }

    public async Task<bool> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(connectionId, cancellationToken);
        if (connection == null)
        {
            _logConnectionNotFoundForTest(_logger, connectionId, null);
            return false;
        }
        return connection.IsActive;
    }

    public async Task<bool> UpdateHealthStatusAsync(Guid connectionId, ConnectionHealthStatus healthStatus, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE honua.data_connections SET
                health_status = @health_status,
                last_health_check = NOW()
            WHERE connection_id = @connection_id
            """;

        try
        {
            await using var connection = await _connectionProvider.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            command.CommandText = sql;
            command.Parameters.Add(new NpgsqlParameter("@connection_id", connectionId));
            command.Parameters.Add(new NpgsqlParameter("@health_status", ToLowerString(healthStatus)));

            var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);

            if (rowsAffected > 0)
            {
                _logHealthStatusUpdated(_logger, connectionId, ToLowerString(healthStatus), null);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logHealthStatusUpdateFailure(_logger, connectionId, ex);
            throw;
        }
    }

    private static void AddConnectionParameters(DbCommand command, DataConnection connection)
    {
        command.Parameters.Add(new NpgsqlParameter("@connection_id", connection.ConnectionId));
        command.Parameters.Add(new NpgsqlParameter("@name", connection.Name));
        command.Parameters.Add(new NpgsqlParameter("@description", (object?)connection.Description ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@host", connection.Host));
        command.Parameters.Add(new NpgsqlParameter("@port", connection.Port));
        command.Parameters.Add(new NpgsqlParameter("@database_name", connection.DatabaseName));
        command.Parameters.Add(new NpgsqlParameter("@username", connection.Username));
        command.Parameters.Add(new NpgsqlParameter("@provider_name", connection.NormalizedProvider));
        command.Parameters.Add(new NpgsqlParameter("@ssl_required", connection.SslRequired));
        command.Parameters.Add(new NpgsqlParameter("@ssl_mode", ToLowerString(connection.SslMode)));
        command.Parameters.Add(new NpgsqlParameter("@connection_string_encrypted", (object?)connection.ConnectionStringEncrypted ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@encryption_key_version", connection.EncryptionKeyVersion));
        command.Parameters.Add(new NpgsqlParameter("@secret_ref", (object?)connection.SecretRef ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@secret_type", (object?)connection.SecretType ?? DBNull.Value));
        command.Parameters.Add(new NpgsqlParameter("@created_by", connection.CreatedBy));
        command.Parameters.Add(new NpgsqlParameter("@is_active", connection.IsActive));
    }

    private static DataConnection ReadDataConnection(DbDataReader reader)
    {
        return new DataConnection
        {
            ConnectionId = reader.GetGuid(0),                // connection_id
            Name = reader.GetString(1),                      // name
            Description = reader.IsDBNull(2) ? null : reader.GetString(2), // description
            Host = reader.GetString(3),                      // host
            Port = reader.GetInt32(4),                       // port
            DatabaseName = reader.GetString(5),              // database_name
            Username = reader.GetString(6),                  // username
            Provider = reader.GetString(7),                  // provider_name
            SslRequired = reader.GetBoolean(8),              // ssl_required
            SslMode = ParseSslMode(reader.GetString(9)), // ssl_mode
            ConnectionStringEncrypted = reader.IsDBNull(10) ? null : (byte[])reader[10], // connection_string_encrypted
            EncryptionKeyVersion = reader.GetInt32(11),      // encryption_key_version
            SecretRef = reader.IsDBNull(12) ? null : reader.GetString(12), // secret_ref
            SecretType = reader.IsDBNull(13) ? null : reader.GetString(13), // secret_type
            CreatedAt = reader.GetDateTime(14),              // created_at
            UpdatedAt = reader.GetDateTime(15),              // updated_at
            CreatedBy = reader.GetString(16),                // created_by
            IsActive = reader.GetBoolean(17),                // is_active
            LastHealthCheck = reader.IsDBNull(18) ? null : reader.GetDateTime(18), // last_health_check
            HealthStatus = Enum.Parse<ConnectionHealthStatus>(reader.GetString(19), true) // health_status
        };
    }

    private static CoreSslMode ParseSslMode(string value) => value.ToLowerInvariant() switch
    {
        "disable" => CoreSslMode.Disable,
        "allow" => CoreSslMode.Allow,
        "prefer" => CoreSslMode.Prefer,
        "require" => CoreSslMode.Require,
        "verifyca" => CoreSslMode.VerifyCa,
        "verify-ca" => CoreSslMode.VerifyCa,
        "verifyfull" => CoreSslMode.VerifyFull,
        "verify-full" => CoreSslMode.VerifyFull,
        _ => throw new InvalidOperationException($"Unsupported SSL mode value '{value}' in secure connection registry.")
    };

    // Interface implementations that delegate to existing methods

    /// <inheritdoc />
    public async Task RegisterConnectionAsync(DataConnection connection)
    {
        await CreateConnectionAsync(connection);
    }

    /// <inheritdoc />
    public async Task<DataConnection?> GetConnectionAsync(string connectionId)
    {
        if (Guid.TryParse(connectionId, out var guid))
        {
            return await GetConnectionAsync(guid);
        }
        return null;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
    {
        return await GetActiveConnectionsAsync();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveConnectionAsync(string connectionId)
    {
        if (Guid.TryParse(connectionId, out var guid))
        {
            return await DeleteConnectionAsync(guid);
        }
        return false;
    }

    /// <inheritdoc />
    public async Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
    {
        var connections = await GetActiveConnectionsAsync();
        var results = new Dictionary<string, ConnectionHealthStatus>();

        foreach (var connection in connections)
        {
            var isHealthy = await TestConnectionAsync(connection.ConnectionId);
            var status = isHealthy ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Unhealthy;
            results[connection.ConnectionId.ToString()] = status;
        }

        return results;
    }

    // Additional interface methods required by ISecureConnectionRegistry

    /// <summary>
    /// Gets a connection by ID with cancellation support (overload).
    /// </summary>
    public async Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return null;

        if (Guid.TryParse(connectionId, out var guid))
            return await GetConnectionAsync(guid, cancellationToken);

        // Fallback: try to find by name
        return await GetConnectionByNameAsync(connectionId, cancellationToken);
    }

    /// <summary>
    /// Updates the health status of a connection using string ID and bool status.
    /// </summary>
    public async Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        if (Guid.TryParse(connectionId, out var guid))
        {
            var status = isHealthy ? ConnectionHealthStatus.Healthy : ConnectionHealthStatus.Unhealthy;
            await UpdateHealthStatusAsync(guid, status, cancellationToken);
        }
    }

    /// <summary>
    /// Gets all active connections (interface compatibility wrapper).
    /// </summary>
    /// <remarks>
    /// The public <see cref="GetActiveConnectionsAsync(CancellationToken)"/> returns
    /// <see cref="IReadOnlyList{T}"/> while the interface contract is
    /// <see cref="IEnumerable{T}"/>. We use an async/await chain (instead of the previous
    /// <c>ContinueWith(...).Result</c> pattern) so cancellation and synchronization
    /// context are preserved, and so failures surface as the underlying exception
    /// rather than an <see cref="AggregateException"/>.
    /// </remarks>
    async Task<IEnumerable<DataConnection>> ISecureConnectionRegistry.GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return await GetActiveConnectionsAsync(cancellationToken).ConfigureAwait(false);
    }
}

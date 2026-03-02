// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Sdk.Admin.Models;

/// <summary>
/// Summary information about a secure database connection (safe for API responses).
/// </summary>
public class SecureConnectionSummary
{
    /// <summary>
    /// Unique identifier for the connection.
    /// </summary>
    [JsonPropertyName("connectionId")]
    public Guid ConnectionId { get; init; }

    /// <summary>
    /// Human-readable name for the connection.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Database server port.
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; init; }

    /// <summary>
    /// Database name.
    /// </summary>
    [JsonPropertyName("databaseName")]
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>
    /// Database username.
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Whether SSL/TLS is required.
    /// </summary>
    [JsonPropertyName("sslRequired")]
    public bool SslRequired { get; init; }

    /// <summary>
    /// SSL connection mode.
    /// </summary>
    [JsonPropertyName("sslMode")]
    public string SslMode { get; init; } = string.Empty;

    /// <summary>
    /// Type of credential storage (managed or external reference).
    /// </summary>
    [JsonPropertyName("storageType")]
    public string StorageType { get; init; } = string.Empty;

    /// <summary>
    /// Whether the connection is currently active.
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    /// <summary>
    /// Current health status.
    /// </summary>
    [JsonPropertyName("healthStatus")]
    public string HealthStatus { get; init; } = string.Empty;

    /// <summary>
    /// Last time a health check was performed.
    /// </summary>
    [JsonPropertyName("lastHealthCheck")]
    public DateTimeOffset? LastHealthCheck { get; init; }

    /// <summary>
    /// When the connection was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Who created the connection.
    /// </summary>
    [JsonPropertyName("createdBy")]
    public string CreatedBy { get; init; } = string.Empty;
}

/// <summary>
/// Detailed information about a secure database connection.
/// </summary>
public sealed class SecureConnectionDetail : SecureConnectionSummary
{
    /// <summary>
    /// Credential reference (if using external secret storage).
    /// </summary>
    [JsonPropertyName("credentialReference")]
    public string? CredentialReference { get; init; }

    /// <summary>
    /// Encryption version used (if using managed storage).
    /// </summary>
    [JsonPropertyName("encryptionVersion")]
    public int EncryptionVersion { get; init; }

    /// <summary>
    /// When the connection was last updated.
    /// </summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>
/// Request model for creating a new secure database connection.
/// </summary>
public sealed class CreateSecureConnectionRequest
{
    /// <summary>
    /// Human-readable name for the connection (must be unique).
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname or IP address.
    /// </summary>
    [JsonPropertyName("host")]
    public string Host { get; init; } = string.Empty;

    /// <summary>
    /// Database server port number.
    /// </summary>
    [JsonPropertyName("port")]
    public int Port { get; init; } = 5432;

    /// <summary>
    /// Database name to connect to.
    /// </summary>
    [JsonPropertyName("databaseName")]
    public string DatabaseName { get; init; } = string.Empty;

    /// <summary>
    /// Database username.
    /// </summary>
    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    /// <summary>
    /// Database password (used only for encrypted storage, not persisted in logs).
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>
    /// External secret manager reference (alternative to Password).
    /// </summary>
    [JsonPropertyName("secretReference")]
    public string? SecretReference { get; init; }

    /// <summary>
    /// Type of secret management system (required when using SecretReference).
    /// </summary>
    [JsonPropertyName("secretType")]
    public string? SecretType { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required for this connection.
    /// </summary>
    [JsonPropertyName("sslRequired")]
    public bool SslRequired { get; init; } = true;

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    [JsonPropertyName("sslMode")]
    public string SslMode { get; init; } = "Require";
}

/// <summary>
/// Request model for updating an existing secure database connection.
/// </summary>
public sealed class UpdateSecureConnectionRequest
{
    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname or IP address.
    /// </summary>
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    /// <summary>
    /// Database server port number.
    /// </summary>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    /// <summary>
    /// Database name to connect to.
    /// </summary>
    [JsonPropertyName("databaseName")]
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Database username.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    /// New database password (optional, only for re-encrypting credentials).
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required for this connection.
    /// </summary>
    [JsonPropertyName("sslRequired")]
    public bool? SslRequired { get; init; }

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    [JsonPropertyName("sslMode")]
    public string? SslMode { get; init; }

    /// <summary>
    /// Whether the connection is active.
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; init; }
}

/// <summary>
/// Result of a connection health test.
/// </summary>
public sealed class ConnectionTestResult
{
    /// <summary>
    /// ID of the connection that was tested.
    /// </summary>
    [JsonPropertyName("connectionId")]
    public Guid ConnectionId { get; init; }

    /// <summary>
    /// Name of the connection that was tested.
    /// </summary>
    [JsonPropertyName("connectionName")]
    public string ConnectionName { get; init; } = string.Empty;

    /// <summary>
    /// Whether the connection test was successful.
    /// </summary>
    [JsonPropertyName("isHealthy")]
    public bool IsHealthy { get; init; }

    /// <summary>
    /// When the test was performed.
    /// </summary>
    [JsonPropertyName("testedAt")]
    public DateTimeOffset TestedAt { get; init; }

    /// <summary>
    /// Human-readable test result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Result of encryption service validation.
/// </summary>
public sealed class EncryptionValidationResult
{
    /// <summary>
    /// Whether the encryption service is working correctly.
    /// </summary>
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    /// <summary>
    /// Current encryption key version.
    /// </summary>
    [JsonPropertyName("currentKeyVersion")]
    public int CurrentKeyVersion { get; init; }

    /// <summary>
    /// When the validation was performed.
    /// </summary>
    [JsonPropertyName("validatedAt")]
    public DateTimeOffset ValidatedAt { get; init; }

    /// <summary>
    /// Human-readable validation result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// Result of encryption key rotation.
/// </summary>
public sealed class KeyRotationResult
{
    /// <summary>
    /// Previous key version.
    /// </summary>
    [JsonPropertyName("previousKeyVersion")]
    public int PreviousKeyVersion { get; init; }

    /// <summary>
    /// New key version after rotation.
    /// </summary>
    [JsonPropertyName("newKeyVersion")]
    public int NewKeyVersion { get; init; }

    /// <summary>
    /// When the key rotation was performed.
    /// </summary>
    [JsonPropertyName("rotatedAt")]
    public DateTimeOffset RotatedAt { get; init; }

    /// <summary>
    /// Human-readable rotation result message.
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

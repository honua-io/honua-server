// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// Represents a secure database connection configuration with encrypted credentials.
/// </summary>
public sealed class DataConnection
{
    /// <summary>
    /// Unique identifier for the connection configuration.
    /// </summary>
    public Guid ConnectionId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for the connection (must be unique).
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname or IP address.
    /// </summary>
    [Required]
    [StringLength(255, MinimumLength = 1)]
    public required string Host { get; init; }

    /// <summary>
    /// Database server port number.
    /// </summary>
    [Range(1, 65535)]
    public int Port { get; init; } = 5432;

    /// <summary>
    /// Database name to connect to.
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string DatabaseName { get; init; }

    /// <summary>
    /// Database username.
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string Username { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required for this connection.
    /// </summary>
    public bool SslRequired { get; init; } = true;

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    public SslMode SslMode { get; init; } = SslMode.Require;

    /// <summary>
    /// Encrypted connection string data (AES-GCM encrypted).
    /// </summary>
    /// <remarks>
    /// This field is mutually exclusive with <see cref="SecretRef"/>.
    /// When using encrypted storage, this contains the encrypted connection string.
    /// </remarks>
    public byte[]? ConnectionStringEncrypted { get; init; }

    /// <summary>
    /// Version of the encryption key used to encrypt the connection string.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int EncryptionKeyVersion { get; init; } = 1;

    /// <summary>
    /// External secret manager reference (alternative to encrypted storage).
    /// </summary>
    /// <remarks>
    /// This field is mutually exclusive with <see cref="ConnectionStringEncrypted"/>.
    /// Format: {provider}:{path} (e.g., "aws:secretsmanager:prod-db-creds")
    /// </remarks>
    [StringLength(255)]
    public string? SecretRef { get; init; }

    /// <summary>
    /// Type of secret management system (when using SecretRef).
    /// </summary>
    [StringLength(32)]
    public string? SecretType { get; init; }

    /// <summary>
    /// When this connection configuration was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this connection configuration was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Who created this connection configuration.
    /// </summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string CreatedBy { get; init; }

    /// <summary>
    /// Whether this connection is currently active and available for use.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Last time a health check was performed on this connection.
    /// </summary>
    public DateTimeOffset? LastHealthCheck { get; init; }

    /// <summary>
    /// Current health status of the connection.
    /// </summary>
    public ConnectionHealthStatus HealthStatus { get; init; } = ConnectionHealthStatus.Unknown;

    /// <summary>
    /// Validates that the connection configuration is valid.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when configuration is invalid</exception>
    public void Validate()
    {
        // Ensure either encrypted storage OR secret reference is provided, but not both
        var hasEncrypted = ConnectionStringEncrypted?.Length > 0;
        var hasSecretRef = !string.IsNullOrWhiteSpace(SecretRef);

        if (hasEncrypted && hasSecretRef)
        {
            throw new InvalidOperationException("Connection cannot have both encrypted connection string and secret reference");
        }

        if (!hasEncrypted && !hasSecretRef)
        {
            throw new InvalidOperationException("Connection must have either encrypted connection string or secret reference");
        }

        // If using secret reference, secret type must be specified
        if (hasSecretRef && string.IsNullOrWhiteSpace(SecretType))
        {
            throw new InvalidOperationException("SecretType must be specified when using SecretRef");
        }

        // Validate SSL mode is compatible with SSL requirement
        if (SslRequired && SslMode == SslMode.Disable)
        {
            throw new InvalidOperationException("SSL cannot be disabled when SslRequired is true");
        }
    }

    /// <summary>
    /// Creates a new connection configuration with encrypted credentials.
    /// </summary>
    /// <param name="name">Connection name</param>
    /// <param name="host">Database host</param>
    /// <param name="port">Database port</param>
    /// <param name="databaseName">Database name</param>
    /// <param name="username">Username</param>
    /// <param name="encryptedConnectionString">Encrypted connection string data</param>
    /// <param name="encryptionKeyVersion">Encryption key version</param>
    /// <param name="createdBy">Creator identity</param>
    /// <param name="description">Optional description</param>
    /// <param name="sslRequired">Whether SSL is required</param>
    /// <param name="sslMode">SSL mode</param>
    /// <returns>New DataConnection instance</returns>
    public static DataConnection CreateWithEncryptedCredentials(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        byte[] encryptedConnectionString,
        int encryptionKeyVersion,
        string createdBy,
        string? description = null,
        bool sslRequired = true,
        SslMode sslMode = SslMode.Require)
    {
        var connection = new DataConnection
        {
            Name = name,
            Host = host,
            Port = port,
            DatabaseName = databaseName,
            Username = username,
            ConnectionStringEncrypted = encryptedConnectionString,
            EncryptionKeyVersion = encryptionKeyVersion,
            CreatedBy = createdBy,
            Description = description,
            SslRequired = sslRequired,
            SslMode = sslMode
        };

        connection.Validate();
        return connection;
    }

    /// <summary>
    /// Creates a new connection configuration with secret manager reference.
    /// </summary>
    /// <param name="name">Connection name</param>
    /// <param name="host">Database host</param>
    /// <param name="port">Database port</param>
    /// <param name="databaseName">Database name</param>
    /// <param name="username">Username</param>
    /// <param name="secretRef">Secret manager reference</param>
    /// <param name="secretType">Secret manager type</param>
    /// <param name="createdBy">Creator identity</param>
    /// <param name="description">Optional description</param>
    /// <param name="sslRequired">Whether SSL is required</param>
    /// <param name="sslMode">SSL mode</param>
    /// <returns>New DataConnection instance</returns>
    public static DataConnection CreateWithSecretReference(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        string secretRef,
        string secretType,
        string createdBy,
        string? description = null,
        bool sslRequired = true,
        SslMode sslMode = SslMode.Require)
    {
        var connection = new DataConnection
        {
            Name = name,
            Host = host,
            Port = port,
            DatabaseName = databaseName,
            Username = username,
            SecretRef = secretRef,
            SecretType = secretType,
            CreatedBy = createdBy,
            Description = description,
            SslRequired = sslRequired,
            SslMode = sslMode
        };

        connection.Validate();
        return connection;
    }
}

/// <summary>
/// SSL/TLS connection modes for database connections.
/// </summary>
public enum SslMode
{
    /// <summary>
    /// SSL is disabled.
    /// </summary>
    Disable,

    /// <summary>
    /// SSL is optional (allow plaintext if SSL unavailable).
    /// </summary>
    Allow,

    /// <summary>
    /// Prefer SSL but allow fallback to plaintext.
    /// </summary>
    Prefer,

    /// <summary>
    /// Require SSL (no plaintext allowed).
    /// </summary>
    Require,

    /// <summary>
    /// Require SSL and verify certificate authority.
    /// </summary>
    VerifyCA,

    /// <summary>
    /// Require SSL and verify full certificate chain.
    /// </summary>
    VerifyFull
}

/// <summary>
/// Health status of a database connection.
/// </summary>
public enum ConnectionHealthStatus
{
    /// <summary>
    /// Health status is unknown (not yet checked).
    /// </summary>
    Unknown,

    /// <summary>
    /// Connection is healthy and responding.
    /// </summary>
    Healthy,

    /// <summary>
    /// Connection is unhealthy or unreachable.
    /// </summary>
    Unhealthy
}

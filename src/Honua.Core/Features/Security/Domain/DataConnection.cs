// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Security.Domain;

/// <summary>
/// Represents a secure data connection configuration.
/// </summary>
public class DataConnection
{
    /// <summary>
    /// Unique identifier for the connection.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display name for the connection.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Connection string for the data source.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Type of database or data source.
    /// </summary>
    public string DatabaseType { get; set; } = string.Empty;

    /// <summary>
    /// Canonical provider engine used to resolve the feature-store implementation.
    /// </summary>
    public string Provider { get; set; } = DataProviderNames.Postgis;

    /// <summary>
    /// Normalized provider engine name used by runtime provider resolution.
    /// </summary>
    public string NormalizedProvider => DataProviderNames.Normalize(Provider);

    /// <summary>
    /// Whether the connection is encrypted.
    /// </summary>
    public bool IsEncrypted { get; set; } = true;

    /// <summary>
    /// Whether the connection is currently active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Additional configuration properties.
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>
    /// Database host.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Database port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// Database name.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Username for authentication.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Encrypted connection string bytes.
    /// </summary>
    public byte[] EncryptedConnectionString { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Encryption key version.
    /// </summary>
    public int EncryptionKeyVersion { get; set; }

    /// <summary>
    /// Who created this connection.
    /// </summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Whether SSL is required.
    /// </summary>
    public bool SslRequired { get; set; }

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    public SslMode SslMode { get; set; }

    // Additional properties needed by PostgresSecureConnectionRegistry
    /// <summary>
    /// Unique connection identifier.
    /// </summary>
    public Guid ConnectionId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Connection description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Encrypted connection string bytes (alias for EncryptedConnectionString).
    /// </summary>
    public byte[]? ConnectionStringEncrypted
    {
        get => EncryptedConnectionString.Length == 0 ? null : EncryptedConnectionString;
        set => EncryptedConnectionString = value ?? Array.Empty<byte>();
    }

    /// <summary>
    /// Secret reference for external secret stores.
    /// </summary>
    public string? SecretRef { get; set; }

    /// <summary>
    /// Type of secret store.
    /// </summary>
    public string? SecretType { get; set; }

    /// <summary>
    /// When the connection was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the connection was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Last health check timestamp.
    /// </summary>
    public DateTime? LastHealthCheck { get; set; }

    /// <summary>
    /// Connection health status.
    /// </summary>
    public ConnectionHealthStatus HealthStatus { get; set; } = ConnectionHealthStatus.Unknown;

    /// <summary>
    /// Creates a data connection with encrypted credentials.
    /// </summary>
    public static DataConnection CreateWithEncryptedCredentials(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        byte[] encryptedConnectionString,
        int encryptionKeyVersion,
        string createdBy)
        => CreateWithEncryptedCredentials(
            name,
            host,
            port,
            databaseName,
            username,
            encryptedConnectionString,
            encryptionKeyVersion,
            createdBy,
            sslRequired: true,
            sslMode: SslMode.Require);

    /// <summary>
    /// Creates a data connection with encrypted credentials.
    /// </summary>
    public static DataConnection CreateWithEncryptedCredentials(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        byte[] encryptedConnectionString,
        int encryptionKeyVersion,
        string createdBy,
        SslMode sslMode)
        => CreateWithEncryptedCredentials(
            name,
            host,
            port,
            databaseName,
            username,
            encryptedConnectionString,
            encryptionKeyVersion,
            createdBy,
            sslRequired: true,
            sslMode: sslMode);

    /// <summary>
    /// Creates a data connection with encrypted credentials.
    /// </summary>
    public static DataConnection CreateWithEncryptedCredentials(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        byte[] encryptedConnectionString,
        int encryptionKeyVersion,
        string createdBy,
        bool sslRequired,
        SslMode sslMode)
    {
        // Validate SSL requirements
        if (sslRequired && (sslMode == SslMode.Allow || sslMode == SslMode.Prefer))
        {
            throw new InvalidOperationException("SSL mode must require encrypted transport when SslRequired is true");
        }

        return new DataConnection
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Host = host,
            Port = port,
            DatabaseName = databaseName,
            Username = username,
            EncryptedConnectionString = encryptedConnectionString,
            EncryptionKeyVersion = encryptionKeyVersion,
            CreatedBy = createdBy,
            SslRequired = sslRequired,
            SslMode = sslMode,
            IsEncrypted = true,
            IsActive = true,
            DatabaseType = "PostgreSQL",
            Provider = DataProviderNames.Postgis
        };
    }

    /// <summary>
    /// Creates a data connection with encrypted credentials and description metadata.
    /// </summary>
    public static DataConnection CreateWithEncryptedCredentials(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        byte[] encryptedConnectionString,
        int encryptionKeyVersion,
        string createdBy,
        string? description,
        bool sslRequired,
        SslMode sslMode)
    {
        var connection = CreateWithEncryptedCredentials(
            name,
            host,
            port,
            databaseName,
            username,
            encryptedConnectionString,
            encryptionKeyVersion,
            createdBy,
            sslRequired,
            sslMode);
        connection.Description = description;
        return connection;
    }

    /// <summary>
    /// Creates a data connection with a secret reference.
    /// </summary>
    public static DataConnection CreateWithSecretReference(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        string secretRef,
        string secretType,
        string createdBy)
        => CreateWithSecretReference(
            name,
            host,
            port,
            databaseName,
            username,
            secretRef,
            secretType,
            createdBy,
            sslRequired: true,
            sslMode: SslMode.Require);

    /// <summary>
    /// Creates a data connection with a secret reference.
    /// </summary>
    public static DataConnection CreateWithSecretReference(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        string secretRef,
        string secretType,
        string createdBy,
        SslMode sslMode)
        => CreateWithSecretReference(
            name,
            host,
            port,
            databaseName,
            username,
            secretRef,
            secretType,
            createdBy,
            sslRequired: true,
            sslMode: sslMode);

    /// <summary>
    /// Creates a data connection with a secret reference.
    /// </summary>
    public static DataConnection CreateWithSecretReference(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        string secretRef,
        string secretType,
        string createdBy,
        bool sslRequired,
        SslMode sslMode)
    {
        // Validate SSL requirements
        if (sslRequired && (sslMode == SslMode.Allow || sslMode == SslMode.Prefer))
        {
            throw new InvalidOperationException("SSL mode must require encrypted transport when SslRequired is true");
        }

        return new DataConnection
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Host = host,
            Port = port,
            DatabaseName = databaseName,
            Username = username,
            SecretRef = secretRef,
            SecretType = secretType,
            CreatedBy = createdBy,
            SslRequired = sslRequired,
            SslMode = sslMode,
            IsEncrypted = false, // Secret will be resolved at runtime
            IsActive = true,
            DatabaseType = "PostgreSQL",
            Provider = DataProviderNames.Postgis
        };
    }

    /// <summary>
    /// Creates a data connection with a secret reference and description metadata.
    /// </summary>
    public static DataConnection CreateWithSecretReference(
        string name,
        string host,
        int port,
        string databaseName,
        string username,
        string secretRef,
        string secretType,
        string createdBy,
        string? description,
        bool sslRequired,
        SslMode sslMode)
    {
        var connection = CreateWithSecretReference(
            name,
            host,
            port,
            databaseName,
            username,
            secretRef,
            secretType,
            createdBy,
            sslRequired,
            sslMode);
        connection.Description = description;
        return connection;
    }

    /// <summary>
    /// Validates the connection configuration.
    /// </summary>
    /// <returns>True if the connection configuration is valid</returns>
    public bool Validate()
    {
        // Basic validation checks
        if (string.IsNullOrWhiteSpace(Name))
            return false;

        if (string.IsNullOrWhiteSpace(Host))
            return false;

        if (Port <= 0 || Port > 65535)
            return false;

        if (string.IsNullOrWhiteSpace(DatabaseName))
            return false;

        if (string.IsNullOrWhiteSpace(Username))
            return false;

        if (string.IsNullOrWhiteSpace(NormalizedProvider))
            return false;

        // SSL validation
        if (SslRequired && (SslMode == SslMode.Allow || SslMode == SslMode.Prefer))
            return false;

        return true;
    }

    /// <summary>
    /// Checks if SSL mode is compatible with SSL requirements.
    /// </summary>
    /// <param name="mode">The SSL mode to check</param>
    /// <param name="requirement">The SSL requirement to check against</param>
    /// <returns>True if SSL mode is compatible with the requirement</returns>
    public static bool IsSslModeCompatibleWithRequirement(SslMode mode, bool requirement)
    {
        if (!requirement)
            return true; // No requirement, any mode is fine

        // SSL is required - check if mode enforces encryption
        return mode switch
        {
            SslMode.Disable => false,
            SslMode.Allow => false,
            SslMode.Prefer => false,
            SslMode.Require => true,
            SslMode.VerifyCa => true,
            SslMode.VerifyFull => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if an SSL requirement is compatible with the selected SSL mode.
    /// </summary>
    /// <param name="requirement">Whether SSL is required.</param>
    /// <param name="mode">The SSL mode to check.</param>
    /// <returns>True if SSL mode is compatible with the requirement.</returns>
    public static bool IsSslModeCompatibleWithRequirement(bool requirement, SslMode mode)
        => IsSslModeCompatibleWithRequirement(mode, requirement);
}

/// <summary>
/// Represents the health status of a connection.
/// </summary>
public enum ConnectionHealthStatus
{
    /// <summary>
    /// Connection is healthy and functioning normally.
    /// </summary>
    Healthy,

    /// <summary>
    /// Connection is experiencing some issues but still functional.
    /// </summary>
    Warning,

    /// <summary>
    /// Connection is not functional.
    /// </summary>
    Unhealthy,

    /// <summary>
    /// Connection status cannot be determined.
    /// </summary>
    Unknown
}

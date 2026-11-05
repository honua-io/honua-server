// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Security.Domain;

public class DataConnection
{
    // The placeholder is not a real host, so a tamper check must treat it as empty rather than compare it to the resolved secret.
    public const string SecretReferenceMetadataPlaceholder = "(from secret reference)";

    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [JsonIgnore]
    public string ConnectionString { get; set; } = string.Empty;

    public string DatabaseType { get; set; } = string.Empty;

    public string Provider { get; set; } = DataProviderNames.Postgis;

    public string NormalizedProvider => DataProviderNames.Normalize(Provider);

    public bool IsEncrypted { get; set; } = true;

    public bool IsActive { get; set; } = true;

    [JsonIgnore]
    public Dictionary<string, object> Properties { get; set; } = new();

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public string DatabaseName { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public byte[] EncryptedConnectionString { get; set; } = Array.Empty<byte>();

    public int EncryptionKeyVersion { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public bool SslRequired { get; set; }

    public SslMode SslMode { get; set; }

    // Additional properties needed by PostgresSecureConnectionRegistry
    public Guid ConnectionId { get; set; } = Guid.NewGuid();

    public string? Description { get; set; }

    public byte[]? ConnectionStringEncrypted
    {
        get => EncryptedConnectionString.Length == 0 ? null : EncryptedConnectionString;
        set => EncryptedConnectionString = value ?? Array.Empty<byte>();
    }

    public string? SecretRef { get; set; }

    public string? SecretType { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastHealthCheck { get; set; }

    public ConnectionHealthStatus HealthStatus { get; set; } = ConnectionHealthStatus.Unknown;

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

    public bool Validate()
    {
        // Basic validation checks
        if (string.IsNullOrWhiteSpace(Name))
            return false;

        if (string.IsNullOrWhiteSpace(Host))
            return false;

        var hasExternalSecret = !IsEncrypted &&
            !string.IsNullOrWhiteSpace(SecretRef) && !string.IsNullOrWhiteSpace(SecretType);
        if (Port < 0 || Port > 65535 || (Port == 0 && !hasExternalSecret))
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

    public static bool IsSslModeCompatibleWithRequirement(bool requirement, SslMode mode)
        => IsSslModeCompatibleWithRequirement(mode, requirement);
}

public enum ConnectionHealthStatus
{
    Healthy,

    Warning,

    Unhealthy,

    Unknown
}

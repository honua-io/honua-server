// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Summary information about a secure database connection (safe for API responses).
/// </summary>
/// <remarks>
/// This model excludes sensitive information like encrypted connection strings
/// or actual secrets, making it safe to return in API responses.
/// </remarks>
public class SecureConnectionSummary
{
    /// <summary>
    /// Unique identifier for the connection.
    /// </summary>
    public required Guid ConnectionId { get; init; }

    /// <summary>
    /// Human-readable name for the connection.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Database server port.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    /// Database name.
    /// </summary>
    public required string DatabaseName { get; init; }

    /// <summary>
    /// Database username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required.
    /// </summary>
    public bool SslRequired { get; init; }

    /// <summary>
    /// SSL connection mode.
    /// </summary>
    public required string SslMode { get; init; }

    /// <summary>
    /// Type of credential storage (managed or external reference).
    /// </summary>
    public required string StorageType { get; init; }

    /// <summary>
    /// Whether the connection is currently active.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Current health status.
    /// </summary>
    public required string HealthStatus { get; init; }

    /// <summary>
    /// Last time a health check was performed.
    /// </summary>
    public DateTimeOffset? LastHealthCheck { get; init; }

    /// <summary>
    /// When the connection was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Who created the connection.
    /// </summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// Detailed information about a secure database connection.
/// </summary>
/// <remarks>
/// Includes additional details not present in the summary view,
/// but still excludes sensitive credential information.
/// </remarks>
public sealed class SecureConnectionDetail : SecureConnectionSummary
{
    /// <summary>
    /// Credential reference (if using external secret storage).
    /// </summary>
    /// <remarks>
    /// Safe to expose as this is just a reference path, not the actual secret.
    /// </remarks>
    public string? CredentialReference { get; init; }

    /// <summary>
    /// Encryption version used (if using managed storage).
    /// </summary>
    public int EncryptionVersion { get; init; }

    /// <summary>
    /// When the connection was last updated.
    /// </summary>
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
    /// Database password (used only for encrypted storage, not persisted in logs).
    /// </summary>
    /// <remarks>
    /// This field should be null when using SecretReference.
    /// The password is encrypted and stored securely, never logged or exposed in responses.
    /// </remarks>
    [StringLength(255)]
    public string? Password { get; init; }

    /// <summary>
    /// External secret manager reference (alternative to Password).
    /// </summary>
    /// <remarks>
    /// Format: {provider}:{path} (e.g., "aws:secretsmanager:prod-db-creds")
    /// This field should be null when using Password.
    /// </remarks>
    [StringLength(255)]
    public string? SecretReference { get; init; }

    /// <summary>
    /// Type of secret management system (required when using SecretReference).
    /// </summary>
    [StringLength(32)]
    public string? SecretType { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required for this connection.
    /// </summary>
    public bool SslRequired { get; init; } = true;

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    [Required]
    public string SslMode { get; init; } = "Require";

    /// <summary>
    /// Validates that the request has either password or secret reference, but not both.
    /// </summary>
    public bool IsValid(out string? validationError)
    {
        var hasPassword = !string.IsNullOrWhiteSpace(Password);
        var hasSecretRef = !string.IsNullOrWhiteSpace(SecretReference);

        if (hasPassword && hasSecretRef)
        {
            validationError = "Cannot specify both Password and SecretReference";
            return false;
        }

        if (!hasPassword && !hasSecretRef)
        {
            validationError = "Must specify either Password or SecretReference";
            return false;
        }

        if (hasSecretRef && string.IsNullOrWhiteSpace(SecretType))
        {
            validationError = "SecretType is required when using SecretReference";
            return false;
        }

        validationError = null;
        return true;
    }
}

/// <summary>
/// Request model for updating an existing secure database connection.
/// </summary>
public sealed class UpdateSecureConnectionRequest
{
    /// <summary>
    /// Optional description of the connection.
    /// </summary>
    [StringLength(1000)]
    public string? Description { get; init; }

    /// <summary>
    /// Database server hostname or IP address.
    /// </summary>
    [StringLength(255, MinimumLength = 1)]
    public string? Host { get; init; }

    /// <summary>
    /// Database server port number.
    /// </summary>
    [Range(1, 65535)]
    public int? Port { get; init; }

    /// <summary>
    /// Database name to connect to.
    /// </summary>
    [StringLength(64, MinimumLength = 1)]
    public string? DatabaseName { get; init; }

    /// <summary>
    /// Database username.
    /// </summary>
    [StringLength(64, MinimumLength = 1)]
    public string? Username { get; init; }

    /// <summary>
    /// New database password (optional, only for re-encrypting credentials).
    /// </summary>
    [StringLength(255)]
    public string? Password { get; init; }

    /// <summary>
    /// Whether SSL/TLS is required for this connection.
    /// </summary>
    public bool? SslRequired { get; init; }

    /// <summary>
    /// SSL mode for the connection.
    /// </summary>
    public string? SslMode { get; init; }

    /// <summary>
    /// Whether the connection is active.
    /// </summary>
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
    public required Guid ConnectionId { get; init; }

    /// <summary>
    /// Name of the connection that was tested.
    /// </summary>
    public required string ConnectionName { get; init; }

    /// <summary>
    /// Whether the connection test was successful.
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// When the test was performed.
    /// </summary>
    public DateTimeOffset TestedAt { get; init; }

    /// <summary>
    /// Human-readable test result message.
    /// </summary>
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
    public bool IsValid { get; init; }

    /// <summary>
    /// Current encryption key version.
    /// </summary>
    public int CurrentKeyVersion { get; init; }

    /// <summary>
    /// When the validation was performed.
    /// </summary>
    public DateTimeOffset ValidatedAt { get; init; }

    /// <summary>
    /// Human-readable validation result message.
    /// </summary>
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
    public int PreviousKeyVersion { get; init; }

    /// <summary>
    /// New key version after rotation.
    /// </summary>
    public int NewKeyVersion { get; init; }

    /// <summary>
    /// When the key rotation was performed.
    /// </summary>
    public DateTimeOffset RotatedAt { get; init; }

    /// <summary>
    /// Human-readable rotation result message.
    /// </summary>
    public string? Message { get; init; }
}

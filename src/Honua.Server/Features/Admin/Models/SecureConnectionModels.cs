// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Server.Features.Admin.Models;

public class SecureConnectionSummary
{
    public required Guid ConnectionId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string Host { get; init; }

    public int Port { get; init; }

    public required string DatabaseName { get; init; }

    public required string Username { get; init; }

    public required string Provider { get; init; }

    public bool SslRequired { get; init; }

    public required string SslMode { get; init; }

    public required string StorageType { get; init; }

    public bool IsActive { get; init; }

    public required string HealthStatus { get; init; }

    public DateTimeOffset? LastHealthCheck { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

// The connection detail never includes credentials.
public sealed class SecureConnectionDetail : SecureConnectionSummary
{
    // A secret reference is a path, not the secret.
    public string? CredentialReference { get; init; }

    public int EncryptionVersion { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class CreateSecureConnectionRequest
{
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public required string Name { get; init; }

    [StringLength(1000)]
    public string? Description { get; init; }

    // The host is required for inline credentials and is display-only when a secret reference holds the connection string.
    [StringLength(255)]
    public string? Host { get; init; }

    // An explicit PostgreSQL port must match the port in the resolved secret.
    [Range(1, 65535)]
    public int? Port { get; init; }

    // The database name is required with a password and is display-only with a secret reference.
    [StringLength(64)]
    public string? DatabaseName { get; init; }

    // The username is required with a password and is display-only with a secret reference.
    [StringLength(64)]
    public string? Username { get; init; }

    [StringLength(32, MinimumLength = 1)]
    public string Provider { get; init; } = DataProviderNames.Postgis;

    // The password is never logged, and it is null when the connection uses a secret reference.
    [StringLength(255)]
    public string? Password { get; init; }

    // The secret reference is null when the connection uses a password.
    [StringLength(255)]
    public string? SecretReference { get; init; }

    [StringLength(32)]
    public string? SecretType { get; init; }

    public bool SslRequired { get; init; } = true;

    [Required]
    public string SslMode { get; init; } = "Require";

    // A connection has a password or a secret reference, not both.
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

        // With an inline password the connection string is assembled from these fields, so they are required.
        // With a secret reference the resolved secret holds the full connection string, so they are optional.
        if (hasPassword &&
            (string.IsNullOrWhiteSpace(Host) ||
             string.IsNullOrWhiteSpace(DatabaseName) ||
             string.IsNullOrWhiteSpace(Username)))
        {
            validationError = "Host, database, and username are required when using a password.";
            return false;
        }

        validationError = null;
        return true;
    }

    internal int GetEffectivePort() => Port ?? (string.IsNullOrWhiteSpace(SecretReference) ? 5432 : 0);
}

public sealed class UpdateSecureConnectionRequest
{
    [StringLength(1000)]
    public string? Description { get; init; }

    [StringLength(255, MinimumLength = 1)]
    public string? Host { get; init; }

    [Range(1, 65535)]
    public int? Port { get; init; }

    [StringLength(64, MinimumLength = 1)]
    public string? DatabaseName { get; init; }

    [StringLength(64, MinimumLength = 1)]
    public string? Username { get; init; }

    [StringLength(255)]
    public string? Password { get; init; }

    public bool? SslRequired { get; init; }

    public string? SslMode { get; init; }

    public bool? IsActive { get; init; }
}

public sealed class ConnectionTestResult
{
    public required Guid ConnectionId { get; init; }

    public required string ConnectionName { get; init; }

    public bool IsHealthy { get; init; }

    public DateTimeOffset TestedAt { get; init; }

    public string? Message { get; init; }
}

public sealed class EncryptionValidationResult
{
    public bool IsValid { get; init; }

    public int CurrentKeyVersion { get; init; }

    public DateTimeOffset ValidatedAt { get; init; }

    public string? Message { get; init; }
}

public sealed class KeyRotationResult
{
    public int PreviousKeyVersion { get; init; }

    public int NewKeyVersion { get; init; }

    public DateTimeOffset RotatedAt { get; init; }

    public string? Message { get; init; }
}

// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Admin.Models;

public sealed record SecureConnectionSummary
{
    public Guid ConnectionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool SslRequired { get; init; }
    public string SslMode { get; init; } = string.Empty;
    public string StorageType { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public string HealthStatus { get; init; } = string.Empty;
    public DateTimeOffset? LastHealthCheck { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}

public sealed record SecureConnectionDetail
{
    public Guid ConnectionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public bool SslRequired { get; init; }
    public string SslMode { get; init; } = string.Empty;
    public string StorageType { get; init; } = string.Empty;
    public string? CredentialReference { get; init; }
    public int EncryptionVersion { get; init; }
    public bool IsActive { get; init; }
    public string HealthStatus { get; init; } = string.Empty;
    public DateTimeOffset? LastHealthCheck { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
}

public sealed record CreateSecureConnectionRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string DatabaseName { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }
    public string? SecretReference { get; init; }
    public string? SecretType { get; init; }
    public bool SslRequired { get; init; }
    public string SslMode { get; init; } = string.Empty;
}

public sealed record UpdateSecureConnectionRequest
{
    public string? Description { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public string? DatabaseName { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public bool? SslRequired { get; init; }
    public string? SslMode { get; init; }
    public bool? IsActive { get; init; }
}

public sealed record ConnectionTestResult
{
    public Guid ConnectionId { get; init; }
    public string ConnectionName { get; init; } = string.Empty;
    public bool IsHealthy { get; init; }
    public DateTimeOffset TestedAt { get; init; }
    public string? Message { get; init; }
}

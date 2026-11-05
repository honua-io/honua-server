// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class CreateAdminApiKeyRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    // A key whose grants are only layer-scoped writes, with no admin or star grant, authenticates as a non-admin and may write only those layers; write grants reject wildcards.
    public IReadOnlyList<string> Permissions { get; init; } = [];

    public DateTimeOffset? ExpiresAt { get; init; }

    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

// Each restriction list replaces that restriction entirely, and the key secret is neither accepted nor returned.
public sealed class UpdateAdminApiKeyRestrictionsRequest
{
    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

public sealed class AdminApiKeyResponse
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string KeyPrefix { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public required string Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public DateTimeOffset? RotatedAt { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    public string? CreatedBy { get; init; }

    public IReadOnlyList<string>? AllowedReferrers { get; init; }

    public IReadOnlyList<string>? AllowedIpRanges { get; init; }
}

public sealed class AdminApiKeySecretResponse
{
    public required AdminApiKeyResponse ApiKey { get; init; }

    public required string Key { get; init; }
}

public sealed class AdminApiKeyEffectivePermissionsResponse
{
    public Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Status { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];

    public bool CanAuthenticate { get; init; }
}

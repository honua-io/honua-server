// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class CreateOAuthClientRequest
{
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    // A public client is minted with no secret.
    public string ClientType { get; init; } = "confidential";

    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    // A token never carries a scope outside this client's set.
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class OAuthClientResponse
{
    public Guid Id { get; init; }

    public required string ClientId { get; init; }

    public required string ClientType { get; init; }

    public required string Name { get; init; }

    public string? SecretPrefix { get; init; }

    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    public required string Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastUsedAt { get; init; }

    public string? CreatedBy { get; init; }
}

public sealed class OAuthClientSecretResponse
{
    public required OAuthClientResponse Client { get; init; }

    // The plaintext secret is returned only when a confidential client is created.
    public string? ClientSecret { get; init; }
}

public sealed class DefineOAuthScopeRequest
{
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Scope { get; init; }

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public sealed class OAuthScopeResponse
{
    public required string Scope { get; init; }

    public required string Description { get; init; }

    public IReadOnlyList<string> Permissions { get; init; } = [];
}

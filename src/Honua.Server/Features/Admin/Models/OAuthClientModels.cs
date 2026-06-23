// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request body to register a first-class OAuth2 client (ADR-0053 Increment 2,
/// #1888).
/// </summary>
public sealed class CreateOAuthClientRequest
{
    /// <summary>Human-readable client name for operator audit and list views.</summary>
    [Required]
    [StringLength(120, MinimumLength = 1)]
    public required string Name { get; init; }

    /// <summary>
    /// Client type: <c>confidential</c> (default — keeps a secret) or <c>public</c>
    /// (native/SPA — no secret minted). Case-insensitive.
    /// </summary>
    public string ClientType { get; init; } = "confidential";

    /// <summary>
    /// Grant types the client may use (e.g. <c>client_credentials</c>). When empty
    /// the client is unrestricted.
    /// </summary>
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    /// <summary>Registered redirect URIs (for interactive grants).</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>
    /// Scope-catalogue scopes this client may request. A token never carries a scope
    /// outside this set (scope narrowing is enforced server-side).
    /// </summary>
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    /// <summary>Optional UTC expiration for the client registration.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>Metadata for a registered OAuth2 client (no plaintext secret).</summary>
public sealed class OAuthClientResponse
{
    /// <summary>Stable registration identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>Public OAuth2 <c>client_id</c>.</summary>
    public required string ClientId { get; init; }

    /// <summary>Client type (<c>confidential</c> or <c>public</c>).</summary>
    public required string ClientType { get; init; }

    /// <summary>Human-readable client name.</summary>
    public required string Name { get; init; }

    /// <summary>Non-secret secret prefix for operator recognition (confidential only).</summary>
    public string? SecretPrefix { get; init; }

    /// <summary>Grant types the client may use.</summary>
    public IReadOnlyList<string> AllowedGrantTypes { get; init; } = [];

    /// <summary>Registered redirect URIs.</summary>
    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    /// <summary>Scopes this client may request.</summary>
    public IReadOnlyList<string> AllowedScopes { get; init; } = [];

    /// <summary>Current lifecycle status.</summary>
    public required string Status { get; init; }

    /// <summary>When the client was registered.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the client metadata was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Optional expiration time.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Most recent successful authentication time.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Authenticated principal that registered the client when available.</summary>
    public string? CreatedBy { get; init; }
}

/// <summary>Create response that includes the one-time plaintext client secret.</summary>
public sealed class OAuthClientSecretResponse
{
    /// <summary>Client metadata without secret material.</summary>
    public required OAuthClientResponse Client { get; init; }

    /// <summary>
    /// Plaintext client secret. Returned only at create time and only for
    /// confidential clients. Null for public clients.
    /// </summary>
    public string? ClientSecret { get; init; }
}

/// <summary>Request body to define (or replace) an OAuth2 scope.</summary>
public sealed class DefineOAuthScopeRequest
{
    /// <summary>Canonical scope name (e.g. <c>features:read</c>).</summary>
    [Required]
    [StringLength(200, MinimumLength = 1)]
    public required string Scope { get; init; }

    /// <summary>Human-readable description shown to operators.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>RBAC permissions this scope grants when included in a token.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

/// <summary>A defined OAuth2 scope and its permission mapping.</summary>
public sealed class OAuthScopeResponse
{
    /// <summary>Canonical scope name.</summary>
    public required string Scope { get; init; }

    /// <summary>Human-readable description.</summary>
    public required string Description { get; init; }

    /// <summary>RBAC permissions this scope grants.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

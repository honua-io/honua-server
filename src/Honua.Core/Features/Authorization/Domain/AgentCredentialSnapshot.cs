// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Authorization.Domain;

/// <summary>Discriminated, sealed approval credential binding; contains no bearer secret.</summary>
public sealed record AgentCredentialSnapshot
{
    /// <summary>Credential kind: api-key or oidc-delegated-agent.</summary>
    public required string Kind { get; init; }
    /// <summary>Stored API key ID or delegation profile ID.</summary>
    public required string Id { get; init; }
    /// <summary>Authentication-time stored credential version; no secret key material.</summary>
    public string? CredentialVersion { get; init; }
    /// <summary>Delegation generation captured at proposal time.</summary>
    public long Generation { get; init; }
    /// <summary>Exact issuer, audience, subject and tenant captured at proposal time.</summary>
    public string[] Binding { get; init; } = [];
    /// <summary>Recognized OAuth scopes from validated token claims; replay remains scope-governed even when empty.</summary>
    public string[] OAuthScopes { get; init; } = [];
    /// <summary>Canonical role-grant hashes captured at proposal time.</summary>
    public string[] RoleGrantFingerprints { get; init; } = [];
    /// <summary>Canonical managed roles captured at enrollment resolution.</summary>
    public string[] MembershipRoles { get; init; } = [];
    /// <summary>Validated bearer token expiry; replay cannot outlive its originating credential.</summary>
    public DateTimeOffset TokenExpiresAt { get; init; }
    /// <summary>Canonical actor captured at proposal time.</summary>
    public string? ActorId { get; init; }
    /// <summary>Granted permissions captured at proposal time.</summary>
    public required string[] Permissions { get; init; }
}

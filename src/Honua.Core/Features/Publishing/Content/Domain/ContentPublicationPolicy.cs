// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Publishing.Content.Domain;

// This policy is the source of truth; the console must not treat publish, share, or embed state as local UI state.
public sealed record ContentPublicationPolicy
{
    [JsonPropertyName("visibility")]
    public ContentPublicationVisibility Visibility { get; init; } = ContentPublicationVisibility.Private;

    [JsonPropertyName("access")]
    public AccessPolicy? Access { get; init; }

    [JsonPropertyName("share")]
    public ContentSharePolicy Share { get; init; } = new();

    [JsonPropertyName("embed")]
    public ContentEmbedPolicy Embed { get; init; } = new();

    [JsonPropertyName("service")]
    public ContentServicePolicy Service { get; init; } = new();

    [JsonPropertyName("publicLink")]
    public ContentPublicLinkPolicy PublicLink { get; init; } = new();
}

// Source-generated JSON does not run property initializers for omitted members, so a policy must be normalized before it is persisted.
public static class ContentPublicationPolicyNormalizer
{
    public static ContentPublicationPolicy Normalize(ContentPublicationPolicy? policy)
    {
        policy ??= new ContentPublicationPolicy();

        // The nested members are declared non-null, but deserialization can leave them
        // null when absent from the payload. Read through nullable views so the
        // null-coalescing below is meaningful rather than a no-op the compiler flags.
        var publicLink = (ContentPublicLinkPolicy?)policy.PublicLink is { } existingLink
            ? existingLink with { Links = (IReadOnlyList<ContentPublicLink>?)existingLink.Links ?? [] }
            : new ContentPublicLinkPolicy();

        return policy with
        {
            Share = (ContentSharePolicy?)policy.Share ?? new ContentSharePolicy(),
            Embed = (ContentEmbedPolicy?)policy.Embed ?? new ContentEmbedPolicy(),
            Service = (ContentServicePolicy?)policy.Service ?? new ContentServicePolicy(),
            PublicLink = publicLink,
        };
    }
}

public sealed record ContentSharePolicy
{
    [JsonPropertyName("allowSharing")]
    public bool AllowSharing { get; init; }

    [JsonPropertyName("allowAnonymous")]
    public bool AllowAnonymous { get; init; }

    [JsonPropertyName("allowedScopes")]
    public IReadOnlyList<string>? AllowedScopes { get; init; }
}

public sealed record ContentEmbedPolicy
{
    [JsonPropertyName("allowEmbedding")]
    public bool AllowEmbedding { get; init; }

    [JsonPropertyName("allowedOrigins")]
    public IReadOnlyList<string>? AllowedOrigins { get; init; }

    [JsonPropertyName("frameAncestors")]
    public IReadOnlyList<string>? FrameAncestors { get; init; }
}

public sealed record ContentServicePolicy
{
    [JsonPropertyName("requireAuthenticatedServices")]
    public bool RequireAuthenticatedServices { get; init; }

    [JsonPropertyName("allowedServiceIds")]
    public IReadOnlyList<string>? AllowedServiceIds { get; init; }
}

// Public links are off by default, and the registry stores an id plus an optional token hash, never the raw token.
public sealed record ContentPublicLinkPolicy
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("links")]
    public IReadOnlyList<ContentPublicLink> Links { get; init; } = [];
}

public sealed record ContentPublicLink
{
    [JsonPropertyName("linkId")]
    public required string LinkId { get; init; }

    // The stored value is a hash of the link token, never the raw token.
    [JsonPropertyName("tokenHash")]
    public string? TokenHash { get; init; }

    [JsonPropertyName("tokenHashAlgorithm")]
    public string? TokenHashAlgorithm { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("revoked")]
    public bool Revoked { get; init; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; init; }
}

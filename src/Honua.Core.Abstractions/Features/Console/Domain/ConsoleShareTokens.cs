// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// A public-link token granting opaque, membership-independent read access to a
/// Console content item.
/// </summary>
/// <remarks>
/// The opaque <see cref="Token"/> value is returned <em>once</em> at mint time
/// and is <see langword="null"/> on every subsequent read (list/projection):
/// the server keeps it for resolution only and never re-discloses it. Callers
/// reference a minted token thereafter by its stable <see cref="TokenId"/>.
/// </remarks>
public sealed record ConsolePublicLinkToken
{
    /// <summary>
    /// Stable identifier used to expire/delete the token. Safe to surface in
    /// lists and audit logs (carries no access capability).
    /// </summary>
    [JsonPropertyName("tokenId")]
    public required string TokenId { get; init; }

    /// <summary>
    /// Opaque token value. Populated only in the mint response; always
    /// <see langword="null"/> in list/projection responses.
    /// </summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>
    /// Identifier of the content item this token grants access to.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Timestamp when the token was minted.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Optional expiry timestamp. <see langword="null"/> means the token does not
    /// expire on its own and remains valid until explicitly revoked.
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// Identifier of the principal that minted the token (audit attribution).
    /// </summary>
    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    /// <summary>
    /// True when the token can no longer resolve — either explicitly revoked
    /// (soft-deleted for audit) or past its <see cref="ExpiresAt"/>. The store
    /// computes this against the current clock on every read.
    /// </summary>
    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; init; }
}

/// <summary>
/// An embed token authorizing an external surface to render a Console content
/// item for a bounded audience and TTL.
/// </summary>
/// <remarks>
/// As with <see cref="ConsolePublicLinkToken"/>, the opaque <see cref="Token"/>
/// value is disclosed only in the mint response and is never re-emitted. Embed
/// tokens are time-windowed and replayable within their TTL (single-use is a
/// post-MVP enhancement).
/// </remarks>
public sealed record ConsoleEmbedToken
{
    /// <summary>
    /// Stable identifier for the embed token (audit/listing).
    /// </summary>
    [JsonPropertyName("tokenId")]
    public required string TokenId { get; init; }

    /// <summary>
    /// Opaque token value. Populated only in the mint response.
    /// </summary>
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    /// <summary>
    /// Identifier of the content item this token authorizes embedding for.
    /// </summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Audience/scope the embed is authorized to render.
    /// </summary>
    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    /// <summary>
    /// Timestamp when the token was minted.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Hard expiry timestamp. Embed tokens always carry a TTL.
    /// </summary>
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Identifier of the principal that minted the token (audit attribution).
    /// </summary>
    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    /// <summary>
    /// True when the token is past its <see cref="ExpiresAt"/> or embedding has
    /// been disabled on the item. Computed against the current clock on read.
    /// </summary>
    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; init; }
}

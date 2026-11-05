// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

// The opaque token value is returned once at mint and is never disclosed again.
public sealed record ConsolePublicLinkToken
{
    [JsonPropertyName("tokenId")]
    public required string TokenId { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; init; }
}

// The embed token value is disclosed only in the mint response.
public sealed record ConsoleEmbedToken
{
    [JsonPropertyName("tokenId")]
    public required string TokenId { get; init; }

    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("createdById")]
    public string? CreatedById { get; init; }

    [JsonPropertyName("isExpired")]
    public bool IsExpired { get; init; }
}

// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

// The projection is computed for the caller and is never stored.
public sealed record ConsoleShareProjection
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("itemName")]
    public required string ItemName { get; init; }

    [JsonPropertyName("itemTitle")]
    public string? ItemTitle { get; init; }

    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }

    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    [JsonPropertyName("publicLinkEnabled")]
    public bool PublicLinkEnabled { get; init; }

    [JsonPropertyName("publicLinkTokens")]
    public IReadOnlyList<ConsolePublicLinkToken> PublicLinkTokens { get; init; } = Array.Empty<ConsolePublicLinkToken>();

    [JsonPropertyName("embedEnabled")]
    public bool EmbedEnabled { get; init; }

    [JsonPropertyName("embedAudience")]
    public ConsoleEmbedAudience? EmbedAudience { get; init; }

    [JsonPropertyName("openDataEligible")]
    public bool OpenDataEligible { get; init; }

    [JsonPropertyName("callerPermissions")]
    public IReadOnlyList<ConsoleContentAction> CallerPermissions { get; init; } = Array.Empty<ConsoleContentAction>();

    [JsonPropertyName("anonymousEligible")]
    public bool AnonymousEligible { get; init; }

    [JsonPropertyName("endpoints")]
    public IReadOnlyDictionary<string, string> Endpoints { get; init; } = new Dictionary<string, string>();

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("updatedById")]
    public string? UpdatedById { get; init; }
}

public sealed record ConsoleShareDependencyConflict
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("itemType")]
    public ConsoleContentItemType? ItemType { get; init; }

    // Anonymous surfaces omit the blocking item's title so a private name is not leaked.
    [JsonPropertyName("itemName")]
    public string? ItemName { get; init; }

    [JsonPropertyName("blockingReason")]
    public required string BlockingReason { get; init; }
}

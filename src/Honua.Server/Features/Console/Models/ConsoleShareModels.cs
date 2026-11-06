// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Server.Features.Console.Models;

public sealed class UpdateShareAccessRequest
{
    [Required]
    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    [JsonPropertyName("allowDependencyConflicts")]
    public bool AllowDependencyConflicts { get; init; }
}

public sealed class MintPublicLinkRequest
{
    [JsonPropertyName("expiresAt")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed class SetEmbedRequest
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("audience")]
    public ConsoleEmbedAudience? Audience { get; init; }

    [JsonPropertyName("allowDependencyConflicts")]
    public bool AllowDependencyConflicts { get; init; }
}

public sealed class MintEmbedTokenRequest
{
    [Required]
    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    [Range(1, 86_400)]
    [JsonPropertyName("ttlSeconds")]
    public int? TtlSeconds { get; init; }
}

public sealed class ConsoleShareDependencyClosureResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("targetTier")]
    public required ConsoleShareAccessTier TargetTier { get; init; }

    [JsonPropertyName("isCompatible")]
    public required bool IsCompatible { get; init; }

    [JsonPropertyName("conflicts")]
    public required IReadOnlyList<ConsoleShareDependencyConflict> Conflicts { get; init; }
}

public sealed class ConsolePublicLinkListResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("tokens")]
    public required IReadOnlyList<ConsolePublicLinkToken> Tokens { get; init; }
}

// The anonymous resolution carries only presentation metadata.
public sealed class ConsolePublicLinkResolutionResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("accessTier")]
    public required ConsoleShareAccessTier AccessTier { get; init; }

    [JsonPropertyName("endpoints")]
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }
}

public sealed class ConsoleEmbedRedeemResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("itemType")]
    public required ConsoleContentItemType ItemType { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("audience")]
    public required ConsoleEmbedAudience Audience { get; init; }

    [JsonPropertyName("expiresAt")]
    public required DateTimeOffset ExpiresAt { get; init; }

    [JsonPropertyName("endpoints")]
    public required IReadOnlyDictionary<string, string> Endpoints { get; init; }
}

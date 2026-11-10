// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Domain;

// The store record is never serialized to the wire, and a persistent store keeps a hash of the token rather than the plaintext.
public sealed record ConsoleShareState
{
    public required string ItemId { get; init; }

    public ConsoleShareAccessTier AccessTier { get; init; } = ConsoleShareAccessTier.Private;

    public bool EmbedEnabled { get; init; }

    public ConsoleEmbedAudience? EmbedAudience { get; init; }

    public IReadOnlyList<ConsolePublicLinkToken> PublicLinkTokens { get; init; } = Array.Empty<ConsolePublicLinkToken>();

    public IReadOnlyList<ConsoleEmbedToken> EmbedTokens { get; init; } = Array.Empty<ConsoleEmbedToken>();

    public DateTimeOffset UpdatedAt { get; init; }

    public string? UpdatedById { get; init; }
}

public sealed record ConsolePublicLinkResolution
{
    public required string ItemId { get; init; }

    public required ConsoleShareAccessTier AccessTier { get; init; }

    public required string TokenId { get; init; }
}

public sealed record ConsoleEmbedTokenResolution
{
    public required string ItemId { get; init; }

    public required ConsoleEmbedAudience Audience { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required string TokenId { get; init; }
}

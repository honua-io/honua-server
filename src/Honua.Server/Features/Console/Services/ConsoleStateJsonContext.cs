// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.EmbedGovernance.Domain;

namespace Honua.Server.Features.Console.Services;

[JsonSerializable(typeof(StoredConsoleShare))]
[JsonSerializable(typeof(StoredConsoleEmbedToken))]
[JsonSerializable(typeof(EmbedKeyRecord))]
internal sealed partial class ConsoleStateJsonContext : JsonSerializerContext;

// Share state as stored. A public link keeps the SHA-256 of its token, so the token cannot be read back.
internal sealed record StoredConsoleShare
{
    public required string ItemId { get; init; }

    public ConsoleShareAccessTier AccessTier { get; init; } = ConsoleShareAccessTier.Private;

    public bool EmbedEnabled { get; init; }

    public ConsoleEmbedAudience? EmbedAudience { get; init; }

    public IReadOnlyList<StoredConsolePublicLink> PublicLinks { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }

    public string? UpdatedById { get; init; }
}

internal sealed record StoredConsolePublicLink(
    string TokenId, string TokenHash, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? CreatedById, bool IsExpired);

// The payload kept beside an embed token's digest in the shared security-token table.
internal sealed record StoredConsoleEmbedToken(
    string TokenId, string ItemId, ConsoleEmbedAudience Audience, DateTimeOffset ExpiresAt);

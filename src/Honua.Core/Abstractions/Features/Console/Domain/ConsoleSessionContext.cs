// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

public sealed record ConsoleUserProfile
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}

public sealed record ConsoleContentPage
{
    [JsonPropertyName("items")]
    public IReadOnlyList<ConsoleContentItem> Items { get; init; } = Array.Empty<ConsoleContentItem>();

    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed record ConsoleSessionContext
{
    [JsonPropertyName("user")]
    public required ConsoleUserProfile User { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    [JsonPropertyName("navigationEntitlements")]
    public IReadOnlyList<ConsoleNavigationEntitlement> NavigationEntitlements { get; init; }
        = Array.Empty<ConsoleNavigationEntitlement>();

    [JsonPropertyName("content")]
    public required ConsoleContentPage Content { get; init; }

    [JsonPropertyName("degraded")]
    public bool Degraded { get; init; }

    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("persistence")]
    public ConsolePersistenceNotice Persistence { get; init; } = ConsolePersistenceNotice.InMemory;
}

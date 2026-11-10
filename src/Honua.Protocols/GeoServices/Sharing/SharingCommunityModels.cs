// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Protocols.GeoServices.Sharing;

internal sealed record GroupContentResponse
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("start")]
    public int Start { get; init; }

    [JsonPropertyName("num")]
    public int Num { get; init; }

    [JsonPropertyName("nextStart")]
    public int NextStart { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<PortalItem> Items { get; init; } = Array.Empty<PortalItem>();
}

internal sealed record GroupResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("access")]
    public required string Access { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    [JsonPropertyName("members")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Members { get; init; }

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("modified")]
    public long Modified { get; init; }
}

internal sealed record GroupOperationResponse
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("groupId")]
    public required string GroupId { get; init; }

    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroupResponse? Group { get; init; }
}

internal sealed record GroupMembershipResponse
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("groupId")]
    public required string GroupId { get; init; }

    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();
}

internal sealed record ItemSharingResponse
{
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    [JsonPropertyName("notSharedWith")]
    public IReadOnlyList<string> NotSharedWith { get; init; } = Array.Empty<string>();

    [JsonPropertyName("sharing")]
    public required ItemSharingState Sharing { get; init; }
}

internal sealed record ItemSharingState
{
    [JsonPropertyName("everyone")]
    public bool Everyone { get; init; }

    [JsonPropertyName("org")]
    public bool Org { get; init; }

    [JsonPropertyName("groups")]
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
}

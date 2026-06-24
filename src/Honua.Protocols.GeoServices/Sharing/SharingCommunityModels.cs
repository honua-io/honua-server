// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.Sharing;

/// <summary>
/// ArcGIS-compatible community group document (#1868) returned by
/// <c>community/groups/{id}</c> and embedded in create responses.
/// </summary>
internal sealed record GroupResponse
{
    /// <summary>Stable group identifier.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Group title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Group description.</summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Owner username.</summary>
    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    /// <summary>Group access tier (<c>private</c>/<c>org</c>/<c>public</c>).</summary>
    [JsonPropertyName("access")]
    public required string Access { get; init; }

    /// <summary>Discovery tags.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Current member usernames.</summary>
    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    /// <summary>Epoch-millisecond creation timestamp.</summary>
    [JsonPropertyName("created")]
    public long Created { get; init; }

    /// <summary>Epoch-millisecond last-modified timestamp.</summary>
    [JsonPropertyName("modified")]
    public long Modified { get; init; }
}

/// <summary>Result of a group create/delete operation.</summary>
internal sealed record GroupOperationResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>The affected group id.</summary>
    [JsonPropertyName("groupId")]
    public required string GroupId { get; init; }

    /// <summary>The created group document (present for createGroup).</summary>
    [JsonPropertyName("group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroupResponse? Group { get; init; }
}

/// <summary>Result of a group membership change.</summary>
internal sealed record GroupMembershipResponse
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    /// <summary>The affected group id.</summary>
    [JsonPropertyName("groupId")]
    public required string GroupId { get; init; }

    /// <summary>The resulting member usernames.</summary>
    [JsonPropertyName("members")]
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();
}

/// <summary>Result of an item share/unshare operation.</summary>
internal sealed record ItemSharingResponse
{
    /// <summary>The affected item id.</summary>
    [JsonPropertyName("itemId")]
    public required string ItemId { get; init; }

    /// <summary>
    /// Group ids the change could not be applied to (ArcGIS share-response shape).
    /// Empty when every requested audience was applied.
    /// </summary>
    [JsonPropertyName("notSharedWith")]
    public IReadOnlyList<string> NotSharedWith { get; init; } = Array.Empty<string>();

    /// <summary>The item's resulting share state.</summary>
    [JsonPropertyName("sharing")]
    public required ItemSharingState Sharing { get; init; }
}

/// <summary>The resolved share state of a portal item.</summary>
internal sealed record ItemSharingState
{
    /// <summary>Whether the item is shared with everyone (public).</summary>
    [JsonPropertyName("everyone")]
    public bool Everyone { get; init; }

    /// <summary>Whether the item is shared with the org (any authenticated user).</summary>
    [JsonPropertyName("org")]
    public bool Org { get; init; }

    /// <summary>Group ids the item is shared with.</summary>
    [JsonPropertyName("groups")]
    public IReadOnlyList<string> Groups { get; init; } = Array.Empty<string>();
}

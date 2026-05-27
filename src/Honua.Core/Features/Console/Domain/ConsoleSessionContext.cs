// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Minimal user profile surfaced through the Console bootstrap response.
/// </summary>
public sealed record ConsoleUserProfile
{
    /// <summary>
    /// Stable user id.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Display name.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Primary email.
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>
    /// Optional avatar URL.
    /// </summary>
    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; init; }

    /// <summary>
    /// Roles claimed by the principal at bootstrap time.
    /// </summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// First page of content items embedded in the Console bootstrap response.
/// </summary>
public sealed record ConsoleContentPage
{
    /// <summary>
    /// Items in this page.
    /// </summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<ConsoleContentItem> Items { get; init; } = Array.Empty<ConsoleContentItem>();

    /// <summary>
    /// Total number of items available across all pages.
    /// </summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>
    /// Opaque cursor for the next page, or null when no more items remain.
    /// </summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// Bootstrap response combining user profile, capabilities, navigation entitlements
/// and an initial content page so the Console can initialize without network
/// waterfalls.
/// </summary>
public sealed record ConsoleSessionContext
{
    /// <summary>
    /// Current user profile.
    /// </summary>
    [JsonPropertyName("user")]
    public required ConsoleUserProfile User { get; init; }

    /// <summary>
    /// Capability strings resolved for the principal (e.g. <c>catalog.read</c>,
    /// <c>studio.edit</c>). These are stable IDs that Console surfaces use to
    /// gate UI affordances.
    /// </summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Per-route entitlement decisions.
    /// </summary>
    [JsonPropertyName("navigationEntitlements")]
    public IReadOnlyList<ConsoleNavigationEntitlement> NavigationEntitlements { get; init; }
        = Array.Empty<ConsoleNavigationEntitlement>();

    /// <summary>
    /// First page of content for catalog rendering.
    /// </summary>
    [JsonPropertyName("content")]
    public required ConsoleContentPage Content { get; init; }

    /// <summary>
    /// True when one or more upstream components degraded the response (e.g.
    /// the content store returned a partial page). Callers can choose to retry
    /// the affected component without re-running the full bootstrap.
    /// </summary>
    [JsonPropertyName("degraded")]
    public bool Degraded { get; init; }

    /// <summary>
    /// Timestamp at which the response was assembled.
    /// </summary>
    [JsonPropertyName("issuedAt")]
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
}

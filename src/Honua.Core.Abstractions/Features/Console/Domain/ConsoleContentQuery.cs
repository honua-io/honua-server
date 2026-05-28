// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Console.Domain;

/// <summary>
/// Filter/cursor parameters for paginated content listing.
/// </summary>
public sealed record ConsoleContentQuery
{
    /// <summary>
    /// Optional substring query applied to <c>name</c>/<c>title</c>/<c>description</c>.
    /// </summary>
    public string? SearchTerm { get; init; }

    /// <summary>
    /// Restrict results to one or more item types.
    /// </summary>
    public IReadOnlyList<ConsoleContentItemType>? ItemTypes { get; init; }

    /// <summary>
    /// Restrict results to one or more visibility scopes.
    /// </summary>
    public IReadOnlyList<ConsoleVisibility>? Visibilities { get; init; }

    /// <summary>
    /// Restrict to a specific owner principal.
    /// </summary>
    public string? OwnerId { get; init; }

    /// <summary>
    /// Restrict to a specific namespace.
    /// </summary>
    public string? Namespace { get; init; }

    /// <summary>
    /// Opaque cursor returned by a previous query.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    /// Maximum items per page. Implementations clamp to a sensible upper bound.
    /// </summary>
    public int Limit { get; init; } = 25;
}

/// <summary>
/// Paginated content listing result.
/// </summary>
public sealed record ConsoleContentListResult
{
    /// <summary>
    /// Items in this page.
    /// </summary>
    public IReadOnlyList<ConsoleContentItem> Items { get; init; } = Array.Empty<ConsoleContentItem>();

    /// <summary>
    /// Total items matching the query across all pages.
    /// </summary>
    public int Total { get; init; }

    /// <summary>
    /// Opaque cursor for the next page, or null when no more items remain.
    /// </summary>
    public string? NextCursor { get; init; }
}

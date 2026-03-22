// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Response model for the feature overview page.
/// </summary>
public sealed class FeatureOverviewResponse
{
    /// <summary>
    /// Current platform edition name.
    /// </summary>
    public required string CurrentEdition { get; init; }

    /// <summary>
    /// All platform features with their enabled/disabled state.
    /// </summary>
    public required FeatureOverviewItem[] Features { get; init; }
}

/// <summary>
/// Individual feature item with edition-gated status.
/// </summary>
public sealed class FeatureOverviewItem
{
    /// <summary>
    /// Unique feature key.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Feature category for grouping.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Brief description of the feature.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether the feature is enabled under the current edition.
    /// </summary>
    public required bool IsEnabled { get; init; }

    /// <summary>
    /// Minimum edition required to enable this feature.
    /// </summary>
    public required string MinimumEdition { get; init; }

    /// <summary>
    /// Upgrade message if the feature is not enabled, null if enabled.
    /// </summary>
    public string? UpgradeMessage { get; init; }
}

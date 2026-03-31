// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Legend metadata for a style suggestion.
/// </summary>
public sealed class LegendInfo
{
    /// <summary>
    /// Legend title (typically the field name or layer name).
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Ordered legend entries.
    /// </summary>
    public required IReadOnlyList<LegendEntry> Entries { get; init; }
}

/// <summary>
/// A single entry in a legend.
/// </summary>
public sealed class LegendEntry
{
    /// <summary>
    /// Display label for this class.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Hex color string (e.g. "#2D69A5").
    /// </summary>
    public required string Color { get; init; }

    /// <summary>
    /// Minimum value for ranged classes (null for categorical).
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum value for ranged classes (null for categorical).
    /// </summary>
    public double? MaxValue { get; init; }
}

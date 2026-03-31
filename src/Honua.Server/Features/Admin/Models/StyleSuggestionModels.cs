// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Request payload for style suggestion.
/// </summary>
public sealed class StyleSuggestionRequest
{
    /// <summary>
    /// Optional: use this field for classification instead of auto-selection.
    /// </summary>
    public string? PreferredField { get; init; }

    /// <summary>
    /// Optional: use this classification method (EqualInterval, Quantile, NaturalBreaks, UniqueValue).
    /// </summary>
    public string? PreferredMethod { get; init; }

    /// <summary>
    /// Optional: use this palette name (Viridis, CartoBold, RdBu).
    /// </summary>
    public string? PreferredPalette { get; init; }

    /// <summary>
    /// Number of classes to generate (default 5, range 2-12).
    /// </summary>
    public int? ClassCount { get; init; }
}

/// <summary>
/// Response payload containing a style suggestion.
/// </summary>
public sealed class StyleSuggestionResponse
{
    /// <summary>
    /// MapLibre v8 style document (ready to apply).
    /// </summary>
    public JsonElement? MapLibreStyle { get; init; }

    /// <summary>
    /// GeoServices drawingInfo JSON (ready to apply).
    /// </summary>
    public JsonElement? DrawingInfo { get; init; }

    /// <summary>
    /// Legend metadata for rendering a legend component.
    /// </summary>
    public StyleSuggestionLegend? Legend { get; init; }

    /// <summary>
    /// Information about the field selected for classification.
    /// </summary>
    public StyleSuggestionFieldInfo? SuggestedField { get; init; }

    /// <summary>
    /// Classification method used (null for geometry-only defaults).
    /// </summary>
    public string? ClassificationMethod { get; init; }

    /// <summary>
    /// Name of the color palette used.
    /// </summary>
    public string? PaletteName { get; init; }

    /// <summary>
    /// Human-readable observations about data analysis.
    /// </summary>
    public string[]? Observations { get; init; }

    /// <summary>
    /// Edition that generated the suggestion.
    /// </summary>
    public string? Edition { get; init; }
}

/// <summary>
/// Legend metadata in the style suggestion response.
/// </summary>
public sealed class StyleSuggestionLegend
{
    /// <summary>
    /// Legend title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Ordered legend entries.
    /// </summary>
    public StyleSuggestionLegendEntry[]? Entries { get; init; }
}

/// <summary>
/// A single legend entry.
/// </summary>
public sealed class StyleSuggestionLegendEntry
{
    /// <summary>
    /// Display label.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Hex color string.
    /// </summary>
    public string? Color { get; init; }

    /// <summary>
    /// Minimum value for ranged classes.
    /// </summary>
    public double? MinValue { get; init; }

    /// <summary>
    /// Maximum value for ranged classes.
    /// </summary>
    public double? MaxValue { get; init; }
}

/// <summary>
/// Information about the suggested classification field.
/// </summary>
public sealed class StyleSuggestionFieldInfo
{
    /// <summary>
    /// Field name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Field data type.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// Reason this field was selected.
    /// </summary>
    public string? Reason { get; init; }
}

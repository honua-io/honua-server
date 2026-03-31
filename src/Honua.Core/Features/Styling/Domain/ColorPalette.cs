// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

/// <summary>
/// Category of color palette.
/// </summary>
public enum PaletteCategory
{
    /// <summary>Ordered gradient for numeric data.</summary>
    Sequential,

    /// <summary>Distinct colors for categorical data.</summary>
    Qualitative,

    /// <summary>Two-tone gradient with meaningful midpoint.</summary>
    Diverging
}

/// <summary>
/// A named color palette with class-count-indexed color arrays.
/// </summary>
public sealed class ColorPalette
{
    /// <summary>
    /// Palette identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Category of the palette.
    /// </summary>
    public required PaletteCategory Category { get; init; }

    /// <summary>
    /// Whether the palette is safe for colorblind users.
    /// </summary>
    public bool IsColorblindSafe { get; init; }

    /// <summary>
    /// Colors indexed by class count. Key = class count, Value = hex color array.
    /// </summary>
    public required IReadOnlyDictionary<int, string[]> Colors { get; init; }

    /// <summary>
    /// Maximum class count with a purpose-designed color set (largest key in <see cref="Colors"/>).
    /// Callers should clamp classification output to this value to avoid repeating colors.
    /// </summary>
    public int MaxClassCount => Colors.Keys.Max();

    /// <summary>
    /// Gets the colors for the specified class count, falling back to the nearest available count.
    /// </summary>
    public string[] GetColors(int classCount)
    {
        if (Colors.TryGetValue(classCount, out var colors))
            return colors;

        // Fall back to nearest available class count
        var nearest = Colors.Keys.OrderBy(k => Math.Abs(k - classCount)).First();
        var available = Colors[nearest];

        if (classCount < nearest)
        {
            // Take first N colors
            return available[..classCount];
        }

        // Repeat last color to fill
        var result = new string[classCount];
        available.CopyTo(result, 0);
        for (var i = available.Length; i < classCount; i++)
            result[i] = available[^1];
        return result;
    }
}

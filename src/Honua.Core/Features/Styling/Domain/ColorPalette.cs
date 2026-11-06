// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Domain;

public enum PaletteCategory
{
    Sequential,

    Qualitative,

    Diverging
}

public sealed class ColorPalette
{
    public required string Name { get; init; }

    public required PaletteCategory Category { get; init; }

    public bool IsColorblindSafe { get; init; }

    public required IReadOnlyDictionary<int, string[]> Colors { get; init; }

    public int MaxClassCount => Colors.Keys.Max();

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

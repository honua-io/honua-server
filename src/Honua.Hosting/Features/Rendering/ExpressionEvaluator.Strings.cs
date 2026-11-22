// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Infrastructure.Rendering;

internal static partial class ExpressionEvaluator
{
    private static string EvaluateDowncase(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return string.Empty;
        }

        return (Evaluate(array[1], properties, zoom)?.ToString() ?? string.Empty).ToLowerInvariant();
    }

    private static bool EvaluateIn(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 3)
        {
            return false;
        }

        var needle = Evaluate(array[1], properties, zoom);
        var haystack = Evaluate(array[2], properties, zoom);

        if (haystack is string haystackText && needle is string needleText)
        {
            return haystackText.Contains(needleText, StringComparison.Ordinal);
        }

        if (haystack is object?[] items)
        {
            return items.Any(item => CompareEqual(item, needle));
        }

        return false;
    }

    private static double EvaluateIndexOf(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 3)
        {
            return -1d;
        }

        var needle = Evaluate(array[1], properties, zoom);
        var haystack = Evaluate(array[2], properties, zoom);
        var rawFrom = array.Length >= 4
            ? (int)Math.Truncate(ConvertToFloat(Evaluate(array[3], properties, zoom), 0f))
            : 0;
        var fromIndex = Math.Max(0, rawFrom);

        if (haystack is string haystackText && needle is string needleText)
        {
            if (fromIndex > haystackText.Length)
            {
                return needleText.Length == 0 ? haystackText.Length : -1d;
            }

            return haystackText.IndexOf(needleText, fromIndex, StringComparison.Ordinal);
        }

        if (haystack is object?[] items)
        {
            for (var i = fromIndex; i < items.Length; i++)
            {
                if (CompareEqual(items[i], needle))
                {
                    return i;
                }
            }
        }

        return -1d;
    }

    private static double EvaluateLength(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return 0d;
        }

        return Evaluate(array[1], properties, zoom) switch
        {
            string s => s.Length,
            object?[] items => items.Length,
            _ => 0d
        };
    }

    private static object EvaluateSlice(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 3)
        {
            return string.Empty;
        }

        var rawInput = Evaluate(array[1], properties, zoom);
        var rawStart = (int)Math.Truncate(ConvertToFloat(Evaluate(array[2], properties, zoom), 0f));

        if (rawInput is string input)
        {
            var start = NormalizeSliceIndex(rawStart, input.Length);
            var end = array.Length >= 4
                ? NormalizeSliceIndex((int)Math.Truncate(ConvertToFloat(Evaluate(array[3], properties, zoom), input.Length)), input.Length)
                : input.Length;
            return end <= start ? string.Empty : input[start..end];
        }

        if (rawInput is object?[] items)
        {
            var start = NormalizeSliceIndex(rawStart, items.Length);
            var end = array.Length >= 4
                ? NormalizeSliceIndex((int)Math.Truncate(ConvertToFloat(Evaluate(array[3], properties, zoom), items.Length)), items.Length)
                : items.Length;
            return end <= start ? Array.Empty<object?>() : items[start..end];
        }

        return string.Empty;
    }

    private static int NormalizeSliceIndex(int index, int length)
    {
        var normalized = index < 0 ? length + index : index;
        return Math.Clamp(normalized, 0, length);
    }
}

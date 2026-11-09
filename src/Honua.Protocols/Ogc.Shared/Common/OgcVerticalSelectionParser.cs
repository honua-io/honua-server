// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using Honua.Infrastructure.Rendering;

namespace Honua.Protocols.Ogc.Common;

internal static class OgcVerticalSelectionParser
{
    private static readonly string[] VerticalAxisLabels = ["z", "elevation", "height"];

    public static bool TryParseWmtsElevationValue(string? resolvedValue, out VerticalSelection? selection)
    {
        selection = null;

        if (string.IsNullOrWhiteSpace(resolvedValue))
        {
            return true;
        }

        var trimmed = resolvedValue.Trim();
        if (!TryParseNumber(trimmed, out var value))
        {
            return false;
        }

        selection = VerticalSelection.FromValue(value, trimmed);
        return true;
    }

    public static bool TryParseTilesSubset(
        string? subset,
        out VerticalSelection? selection,
        out bool isVerticalAxis,
        out string? errorMessage)
    {
        selection = null;
        isVerticalAxis = false;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(subset))
        {
            return false;
        }

        var trimmed = subset.Trim();

        var open = trimmed.IndexOf('(');
        if (open <= 0 || !trimmed.EndsWith(')'))
        {
            // Not the axis(value) form at all; let the caller's unsupported-subset path handle it.
            return false;
        }

        var axis = trimmed[..open].Trim();
        if (!IsVerticalAxis(axis))
        {
            // A known-but-non-vertical (or unknown) axis such as E(...) / x(...): the caller
            // must still reject this with 400 to preserve the CITE unknown-subset assertion.
            return false;
        }

        isVerticalAxis = true;

        var inner = trimmed[(open + 1)..^1].Trim();
        if (inner.Length == 0)
        {
            errorMessage = "The subset vertical axis requires a value.";
            return false;
        }

        var separator = inner.IndexOf(':');
        if (separator < 0)
        {
            if (!TryParseNumber(inner, out var single))
            {
                errorMessage = "Invalid vertical subset value.";
                return false;
            }

            selection = VerticalSelection.FromValue(single, trimmed);
            return true;
        }

        var lowToken = inner[..separator].Trim();
        var highToken = inner[(separator + 1)..].Trim();
        if (!TryParseNumber(lowToken, out var low) || !TryParseNumber(highToken, out var high))
        {
            errorMessage = "Invalid vertical subset interval.";
            return false;
        }

        if (low > high)
        {
            errorMessage = "Invalid vertical subset interval: low bound exceeds high bound.";
            return false;
        }

        selection = new VerticalSelection(low, high, trimmed);
        return true;
    }

    private static bool IsVerticalAxis(string axis)
        => VerticalAxisLabels.Any(label => string.Equals(axis, label, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseNumber(string value, out double parsed)
        => double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out parsed)
            && !double.IsNaN(parsed)
            && !double.IsInfinity(parsed);
}

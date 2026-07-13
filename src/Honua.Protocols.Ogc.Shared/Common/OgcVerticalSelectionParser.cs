// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using Honua.Infrastructure.Rendering;

namespace Honua.Protocols.Ogc.Common;

/// <summary>
/// Parses elevation/vertical selections from the WMTS <c>elevation=</c> KVP value and the
/// OGC API Tiles <c>subset</c> KVP value into the protocol-neutral
/// <see cref="VerticalSelection"/> value type. Shared so both the WMTS and OGC API Tiles
/// adapters reuse one implementation (AGENTS.md DRY rule). AOT-safe: invariant-culture
/// numeric parsing only, no reflection or dynamic dispatch.
/// </summary>
internal static class OgcVerticalSelectionParser
{
    /// <summary>
    /// The OGC API Tiles subset axis labels that select the vertical/elevation dimension.
    /// Matched case-insensitively.
    /// </summary>
    private static readonly string[] VerticalAxisLabels = ["z", "elevation", "height"];

    /// <summary>
    /// Parses a single already-resolved WMTS <c>elevation</c> dimension value (e.g. <c>"200"</c>)
    /// into a <see cref="VerticalSelection"/>. The WMTS dimension validator has already accepted
    /// the value against the layer's advertised discrete elevation values; this resolver only
    /// converts the validated token to a numeric selection so it can be recorded and threaded
    /// through the render call.
    /// </summary>
    /// <param name="resolvedValue">The validated, resolved elevation token (never <c>default</c>/<c>current</c>).</param>
    /// <param name="selection">The parsed selection on success; otherwise <c>null</c>.</param>
    /// <returns><see langword="true"/> when the value is empty or parses as a number; otherwise <see langword="false"/>.</returns>
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

    /// <summary>
    /// Attempts to interpret an OGC API Tiles <c>subset</c> KVP value as a vertical
    /// (elevation) selection.
    /// </summary>
    /// <remarks>
    /// <para>Accepted axes are <c>Z</c>, <c>elevation</c>, and <c>height</c> (case-insensitive).
    /// Accepted value forms are a single value <c>Z(100)</c> or a closed interval
    /// <c>Z(100:300)</c> using the OGC subset interval separator <c>:</c>.</para>
    /// <para>The three outcomes are distinguished so the caller can preserve the CITE
    /// contract that an <em>unknown / non-vertical</em> subset axis (e.g. <c>E(0:1)</c>) must
    /// still be rejected with 400:</para>
    /// <list type="bullet">
    /// <item><description><paramref name="isVerticalAxis"/> <c>false</c>: the axis is not a
    /// vertical axis (or the value is empty / not in <c>axis(value)</c> form). The caller should
    /// fall back to its existing unsupported-subset handling.</description></item>
    /// <item><description><paramref name="isVerticalAxis"/> <c>true</c> and the return value
    /// <c>true</c>: a valid vertical selection parsed into <paramref name="selection"/>.</description></item>
    /// <item><description><paramref name="isVerticalAxis"/> <c>true</c> and the return value
    /// <c>false</c>: the axis is vertical but the value is malformed
    /// (<paramref name="errorMessage"/> set) — the caller should reject with 400.</description></item>
    /// </list>
    /// </remarks>
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

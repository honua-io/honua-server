// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Shared.Models;
using Honua.Geoprocessing;

namespace Honua.Ai.Protocols.Mcp.MapTools;

/// <summary>
/// Shared plausibility guard for MCP tools that accept a <c>bbox</c> paired with a
/// <c>bboxSrid</c> (<see cref="QueryFeaturesTool"/>, <see cref="RenderMapTool"/>).
/// An LLM agent's most common CRS mistake is a coordinate/SRID mismatch — e.g. a
/// Web&#160;Mercator (metre) bbox submitted with the default geographic
/// <c>bboxSrid</c>&#160;4326, which silently queries the wrong window and returns 0
/// features with no signal, or a lon/lat degree bbox tagged with a projected SRID.
/// Both produce plausible-but-wrong answers rather than an error.
/// </summary>
/// <remarks>
/// The heuristic compares the bbox ordinate ranges against the geographic bounds
/// (|longitude|&#160;&lt;=&#160;180, |latitude|&#160;&lt;=&#160;90) and the SRID's
/// geographic-ness (via the canonical <see cref="GeographicSridClassifier"/>, never a
/// local SRID allowlist — see #2732):
/// <list type="bullet">
///   <item>
///     <description>
///       A geographic SRID whose ordinates fall <b>outside</b> the geographic range is
///       almost certainly projected data mislabelled as lon/lat (the Web Mercator ×
///       4326 case). Rejected.
///     </description>
///   </item>
///   <item>
///     <description>
///       A projected SRID whose ordinates fall <b>entirely inside</b> the geographic
///       range is almost certainly lon/lat degrees mislabelled as projected (real
///       projected extents are in metres/feet and dwarf ±180). Rejected. Unlisted
///       geographic codes (the EPSG 4000–4999 block) are treated as geographic here so
///       this branch never fires on a genuine — if uncommon — geographic query.
///     </description>
///   </item>
/// </list>
/// Rather than silently returning the wrong window, both mismatches raise a
/// <see cref="GeoprocessingValidationException"/> naming the exact bbox values and the
/// SRID, which the MCP error mapper surfaces as a structured <c>invalid_argument</c>
/// tool error.
/// </remarks>
internal static class McpBboxCrsGuard
{
    private const double MaxLongitudeDegrees = 180.0;
    private const double MaxLatitudeDegrees = 90.0;

    /// <summary>
    /// Validates that a <c>[minX, minY, maxX, maxY]</c> bbox is plausibly expressed in
    /// the coordinate system named by <paramref name="srid"/>. The caller is expected to
    /// have already validated arity (four ordinates) and min/max ordering.
    /// </summary>
    /// <param name="minX">Minimum X / longitude ordinate.</param>
    /// <param name="minY">Minimum Y / latitude ordinate.</param>
    /// <param name="maxX">Maximum X / longitude ordinate.</param>
    /// <param name="maxY">Maximum Y / latitude ordinate.</param>
    /// <param name="srid">Declared SRID/WKID for the bbox ordinates.</param>
    /// <exception cref="GeoprocessingValidationException">
    /// Thrown when the bbox ordinates are implausible for the declared SRID.
    /// </exception>
    public static void Validate(double minX, double minY, double maxX, double maxY, int srid)
    {
        // Use the broad geographic-block predicate (not the narrow axis-order list) so an
        // unlisted geographic code (e.g. EPSG:4301) is treated as geographic and never
        // trips the "projected but in-range" branch below.
        var geographic = GeographicSridClassifier.IsGeographicOrUnlistedGeographicRangeSrid(srid);
        var withinGeographicRange =
            Math.Abs(minX) <= MaxLongitudeDegrees && Math.Abs(maxX) <= MaxLongitudeDegrees
            && Math.Abs(minY) <= MaxLatitudeDegrees && Math.Abs(maxY) <= MaxLatitudeDegrees;

        if (geographic && !withinGeographicRange)
        {
            throw new GeoprocessingValidationException(string.Format(
                CultureInfo.InvariantCulture,
                "'bbox' [{0}, {1}, {2}, {3}] has ordinates outside the geographic range "
                + "(±180 longitude / ±90 latitude) but 'bboxSrid' {4} is a geographic (lon/lat degrees) CRS. "
                + "The bbox looks like a projected CRS (e.g. Web Mercator metres) mislabelled as degrees; "
                + "pass the matching projected 'bboxSrid', or supply the bbox in lon/lat degrees.",
                minX, minY, maxX, maxY, srid));
        }

        if (!geographic && withinGeographicRange)
        {
            throw new GeoprocessingValidationException(string.Format(
                CultureInfo.InvariantCulture,
                "'bbox' [{0}, {1}, {2}, {3}] is entirely within the geographic range "
                + "(±180 longitude / ±90 latitude) but 'bboxSrid' {4} is a projected CRS whose coordinates are "
                + "in linear units (e.g. metres). The bbox looks like lon/lat degrees mislabelled as projected; "
                + "pass 'bboxSrid':4326 for degrees, or supply the bbox in the projected CRS units.",
                minX, minY, maxX, maxY, srid));
        }
    }
}
